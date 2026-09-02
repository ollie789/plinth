using System.Diagnostics;
using Plinth.Core;
using Plinth.Pipeline;
using Plinth.Pipeline.Fetch;
using Plinth.Pipeline.Stores;

namespace Plinth.Api;

/// <summary>Builds the API so tests and the CLI's `plinth api` can host it the same way.</summary>
public static class ApiHost
{
    public const string ImmutableCache = "public, max-age=31536000, immutable";

    /// <summary>Every response that is not a servable image: never let a CDN keep one.</summary>
    public const string NoCache = "no-store";

    /// <summary>Header carrying the HMAC over the body's sha256 when <c>PLINTH_SIGNING_KEY</c> is set.</summary>
    public const string SignatureHeader = "X-Plinth-Signature";

    /// <summary>
    /// The category every request line is written under. <c>ApiHost</c> is static, so there is
    /// no <c>ILogger&lt;T&gt;</c> to take: the category is named once here instead.
    /// </summary>
    public const string LogCategory = "Plinth.Api";

    public static WebApplication Build(string[] args, PipelineOptions? options = null, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        options ??= PipelineOptions.FromEnvironment(Environment.GetEnvironmentVariable);
        Engine.Init(options.Concurrency ?? Environment.ProcessorCount);

        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole();
        // ASP.NET's own per-request lines are Information and quote the whole query string,
        // sig included; the one line Observe writes below is the request log, so keep the
        // framework's to warnings and worse.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = options.Fetch.MaxBytes);
        builder.Services.AddSingleton(options);
        // One gate for the whole process: requests beyond it queue instead of being rejected,
        // so a burst costs latency, and peak memory is bounded by MaxInFlight rather than by
        // however many connections Kestrel happens to be holding.
        builder.Services.AddSingleton(new SemaphoreSlim(options.MaxInFlight));
        builder.Services.AddSingleton<ApiStats>();
        builder.Services.AddSingleton(sp => sp.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory));
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
        app.MapGet("/healthz", (ApiStats stats) =>
            Results.Ok(new { status = "ok", hits = stats.Hits, misses = stats.Misses, failed = stats.Failed }));

        app.MapGet("/version", (PipelineOptions o) => Results.Ok(new
        {
            engineVersion = Engine.Version,
            libvipsVersion = Engine.LibvipsVersion,
            recipes = o.Recipes.Names,
        }));

        // HEAD is how a CDN and a health prober ask "is this key servable?" without paying for
        // the bytes; it runs the same pipeline and returns the same headers with no body.
        app.MapMethods("/v1/image", [HttpMethods.Get, HttpMethods.Head],
            async (string? src, string? recipe, string? sig, PlinthPipeline pipeline, PipelineOptions o, SemaphoreSlim gate,
                   ApiStats stats, ILogger log, HttpContext http, CancellationToken ct) =>
        {
            if (Rejected(http, src, sig, o) is { } bad) return bad;

            await gate.WaitAsync(ct);
            try
            {
                var started = Stopwatch.GetTimestamp();
                var result = await pipeline.ProcessUrlAsync(src!, recipe, ct);
                Observe(log, stats, "/v1/image", src!, result, started);
                Stamp(http, result);
                if (result.Status == "failed")
                {
                    http.Response.Headers.CacheControl = NoCache;
                    return o.OnFailure == "redirect"
                        ? Results.Redirect(src!, permanent: false)
                        : Results.Text(result.Record.ToJson(), "application/json", statusCode: StatusCodes.Status502BadGateway);
                }

                http.Response.Headers.CacheControl = ImmutableCache;
                // The key already identifies the exact bytes (source content + recipe + engine),
                // so it is a strong ETag by construction — no hashing of the output needed.
                http.Response.Headers.ETag = $"\"{result.Record.Key}\"";
                var mime = ImageFormats.MimeTypeFor(result.Record.Output!.Format);
                if (HttpMethods.IsHead(http.Request.Method))
                {
                    http.Response.ContentType = mime;
                    http.Response.ContentLength = result.Bytes!.Length;
                    return Results.Empty;
                }
                return Results.Bytes(result.Bytes!, mime);
            }
            finally { gate.Release(); }
        });

        app.MapGet("/v1/inspect", async (string? src, string? recipe, string? sig, PlinthPipeline pipeline, PipelineOptions o,
                                         SemaphoreSlim gate, ApiStats stats, ILogger log, HttpContext http, CancellationToken ct) =>
        {
            if (Rejected(http, src, sig, o) is { } bad) return bad;

            await gate.WaitAsync(ct);
            try
            {
                var started = Stopwatch.GetTimestamp();
                var result = await pipeline.InspectUrlAsync(src!, recipe, ct);
                Observe(log, stats, "/v1/inspect", src!, result, started);
                http.Response.Headers.CacheControl = NoCache;
                return Results.Text(result.Record.ToJson(), "application/json");
            }
            finally { gate.Release(); }
        });

        // The gate is taken before the body is read, not after: a queued upload that already
        // held its bytes in memory would defeat the point of bounding work in flight.
        app.MapPost("/v1/normalize", async (string? recipe, PlinthPipeline pipeline, PipelineOptions o, SemaphoreSlim gate,
                                            ApiStats stats, ILogger log, HttpContext http, CancellationToken ct) =>
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
                        return NoStore(http, Results.Json(new { error = "body too large" }, statusCode: StatusCodes.Status413PayloadTooLarge));
                    ms.Write(buffer, 0, read);
                }
                var body = ms.ToArray();
                if (o.SigningKey is not null
                    && !RequestSigning.VerifyBody(body, http.Request.Headers[SignatureHeader].ToString(), o.SigningKey))
                    return NoStore(http, Results.Json(new { error = "bad signature" }, statusCode: StatusCodes.Status403Forbidden));
                if (body.Length == 0) return NoStore(http, Results.BadRequest(new { error = "empty body" }));

                var started = Stopwatch.GetTimestamp();
                var result = await pipeline.ProcessBytesAsync(body, recipe, null, ct);
                Observe(log, stats, "/v1/normalize", null, result, started);
                Stamp(http, result);
                if (result.Status == "failed")
                {
                    http.Response.Headers.CacheControl = NoCache;
                    return Results.Text(result.Record.ToJson(), "application/json", statusCode: StatusCodes.Status422UnprocessableEntity);
                }
                return Results.Bytes(result.Bytes!, ImageFormats.MimeTypeFor(result.Record.Output!.Format));
            }
            finally { gate.Release(); }
        });
    }

    /// <summary>
    /// The three checks every <c>src</c> route makes before any work: present, signed, on the
    /// allowlist — in that order, so an unsigned caller cannot probe which hosts are configured.
    /// The redirect fallback on failure is why the allowlist matters here and not only in the
    /// fetcher: it must never send a client to an arbitrary host.
    /// </summary>
    private static IResult? Rejected(HttpContext http, string? src, string? sig, PipelineOptions o)
    {
        if (string.IsNullOrEmpty(src)) return NoStore(http, Results.BadRequest(new { error = "src is required" }));
        if (o.SigningKey is not null && !RequestSigning.Verify(src, sig, o.SigningKey))
            return NoStore(http, Results.Json(new { error = "bad signature" }, statusCode: StatusCodes.Status403Forbidden));
        if (!Uri.TryCreate(src, UriKind.Absolute, out var uri) || !o.Fetch.Allows(uri))
            return NoStore(http, Results.BadRequest(new { error = "source not allowed" }));
        return null;
    }

    private static IResult NoStore(HttpContext http, IResult result)
    {
        http.Response.Headers.CacheControl = NoCache;
        return result;
    }

    /// <summary>
    /// One Information line per pipeline call. <c>Host</c> is the source host and nothing more:
    /// the full URL would put a signed query string (and whatever a retailer puts in a path)
    /// into the log stream.
    /// </summary>
    private static void Observe(ILogger log, ApiStats stats, string route, string? src, PipelineResult r, long started)
    {
        stats.Observe(r.Status, r.FromStore);
        log.LogInformation("plinth request {Route} {Host} {Key} {Status} {Cache} {Ms}",
            route,
            src is not null && Uri.TryCreate(src, UriKind.Absolute, out var uri) ? uri.Host : "-",
            r.Record.Key,
            r.Status,
            r.FromStore ? "hit" : "miss",
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

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
