using Plinth.Core;
using Plinth.Pipeline;
using Plinth.Pipeline.Fetch;
using Plinth.Pipeline.Stores;

namespace Plinth.Api;

/// <summary>Builds the API so tests and the CLI's `plinth api` can host it the same way.</summary>
public static class ApiHost
{
    public const string ImmutableCache = "public, max-age=31536000, immutable";

    /// <summary>Header carrying the HMAC over the body's sha256 when <c>PLINTH_SIGNING_KEY</c> is set.</summary>
    public const string SignatureHeader = "X-Plinth-Signature";

    public static WebApplication Build(string[] args, PipelineOptions? options = null, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        options ??= PipelineOptions.FromEnvironment(Environment.GetEnvironmentVariable);
        Engine.Init(options.Concurrency ?? Environment.ProcessorCount);

        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = options.Fetch.MaxBytes);
        builder.Services.AddSingleton(options);
        // One gate for the whole process: requests beyond it queue instead of being rejected,
        // so a burst costs latency, and peak memory is bounded by MaxInFlight rather than by
        // however many connections Kestrel happens to be holding.
        builder.Services.AddSingleton(new SemaphoreSlim(options.MaxInFlight));
        builder.Services.AddSingleton<ISourceFetcher>(_ => new HttpSourceFetcher(options.Fetch));
        builder.Services.AddSingleton<IOutputStore>(_ => StoreUri.Open(options.StoreUri, Environment.GetEnvironmentVariable));
        builder.Services.AddSingleton(sp => new PlinthPipeline(sp.GetRequiredService<ISourceFetcher>(), sp.GetRequiredService<IOutputStore>(), options.Recipes));
        configure?.Invoke(builder);

        var app = builder.Build();
        Map(app);
        return app;
    }

    private static void Map(WebApplication app)
    {
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

        app.MapGet("/version", (PipelineOptions o) => Results.Ok(new
        {
            engineVersion = Engine.Version,
            libvipsVersion = Engine.LibvipsVersion,
            recipes = o.Recipes.Names,
        }));

        app.MapGet("/v1/image", async (string? src, string? recipe, string? sig, PlinthPipeline pipeline, PipelineOptions o, SemaphoreSlim gate, HttpContext http, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(src)) return Results.BadRequest(new { error = "src is required" });
            if (o.SigningKey is not null && !RequestSigning.Verify(src, sig, o.SigningKey))
                return Results.Json(new { error = "bad signature" }, statusCode: StatusCodes.Status403Forbidden);
            if (SourceNotAllowed(src, o) is { } notAllowed) return notAllowed;

            await gate.WaitAsync(ct);
            try
            {
                var result = await pipeline.ProcessUrlAsync(src, recipe, ct);
                Stamp(http, result);
                if (result.Status == "failed")
                    return o.OnFailure == "redirect"
                        ? Results.Redirect(src, permanent: false)
                        : Results.Text(result.Record.ToJson(), "application/json", statusCode: StatusCodes.Status502BadGateway);

                http.Response.Headers.CacheControl = ImmutableCache;
                return Results.Bytes(result.Bytes!, ImageFormats.MimeTypeFor(result.Record.Output!.Format));
            }
            finally { gate.Release(); }
        });

        app.MapGet("/v1/inspect", async (string? src, string? recipe, string? sig, PlinthPipeline pipeline, PipelineOptions o, SemaphoreSlim gate, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(src)) return Results.BadRequest(new { error = "src is required" });
            if (o.SigningKey is not null && !RequestSigning.Verify(src, sig, o.SigningKey))
                return Results.Json(new { error = "bad signature" }, statusCode: StatusCodes.Status403Forbidden);
            if (SourceNotAllowed(src, o) is { } notAllowed) return notAllowed;

            await gate.WaitAsync(ct);
            try
            {
                var result = await pipeline.InspectUrlAsync(src, recipe, ct);
                return Results.Text(result.Record.ToJson(), "application/json");
            }
            finally { gate.Release(); }
        });

        // The gate is taken before the body is read, not after: a queued upload that already
        // held its bytes in memory would defeat the point of bounding work in flight.
        app.MapPost("/v1/normalize", async (string? recipe, PlinthPipeline pipeline, PipelineOptions o, SemaphoreSlim gate, HttpContext http, CancellationToken ct) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var ms = new MemoryStream();
                var buffer = new byte[81920];
                int read;
                while ((read = await http.Request.Body.ReadAsync(buffer, ct)) > 0)
                {
                    if (ms.Length + read > o.Fetch.MaxBytes)
                        return Results.Json(new { error = "body too large" }, statusCode: StatusCodes.Status413PayloadTooLarge);
                    ms.Write(buffer, 0, read);
                }
                var body = ms.ToArray();
                if (o.SigningKey is not null
                    && !RequestSigning.VerifyBody(body, http.Request.Headers[SignatureHeader].ToString(), o.SigningKey))
                    return Results.Json(new { error = "bad signature" }, statusCode: StatusCodes.Status403Forbidden);
                if (body.Length == 0) return Results.BadRequest(new { error = "empty body" });

                var result = await pipeline.ProcessBytesAsync(body, recipe, null, ct);
                Stamp(http, result);
                if (result.Status == "failed")
                    return Results.Text(result.Record.ToJson(), "application/json", statusCode: StatusCodes.Status422UnprocessableEntity);
                return Results.Bytes(result.Bytes!, ImageFormats.MimeTypeFor(result.Record.Output!.Format));
            }
            finally { gate.Release(); }
        });
    }

    /// <summary>
    /// The redirect fallback on failure must never send a client to an arbitrary host: gate
    /// <c>src</c> against the same allowlist the fetcher itself enforces, before the pipeline runs.
    /// </summary>
    private static IResult? SourceNotAllowed(string src, PipelineOptions o) =>
        Uri.TryCreate(src, UriKind.Absolute, out var uri) && o.Fetch.Allows(uri)
            ? null
            : Results.BadRequest(new { error = "source not allowed" });

    private static void Stamp(HttpContext http, PipelineResult r)
    {
        var h = http.Response.Headers;
        h["X-Plinth-Key"] = r.Record.Key;
        h["X-Plinth-Status"] = r.Status;
        h["X-Plinth-Cache"] = r.FromStore ? "hit" : "miss";
        h["X-Plinth-Verdict"] = r.Record.Verdict.PackShot ? "true" : "false";
        h["X-Plinth-Confidence"] = r.Record.Verdict.Confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
    }
}
