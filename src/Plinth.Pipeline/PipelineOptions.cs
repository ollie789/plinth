using Plinth.Core;
using Plinth.Pipeline.Fetch;

namespace Plinth.Pipeline;

/// <summary>Everything the front doors read from the environment (spec 6.1).</summary>
public sealed record PipelineOptions(
    FetchPolicy Fetch,
    string StoreUri,
    RecipeCatalog Recipes,
    string? SigningKey,
    string OnFailure,
    int? Concurrency,
    int MaxInFlight = PipelineOptions.DefaultMaxInFlight,
    bool NormalizeEnabled = false,
    int MaxPixels = SourceInspector.MaxPixels)
{
    public const int DefaultMaxInFlight = 4;

    public static PipelineOptions FromEnvironment(Func<string, string?> env)
    {
        var onFailure = env("PLINTH_ON_FAILURE") ?? "redirect";
        if (onFailure is not ("redirect" or "error")) throw new PlinthException("PLINTH_ON_FAILURE must be redirect or error");
        var recipesPath = env("PLINTH_RECIPES");
        var recipes = string.IsNullOrEmpty(recipesPath) ? RecipeCatalog.DefaultOnly : RecipeCatalog.FromJson(ReadRecipes(recipesPath));
        int? concurrency = int.TryParse(env("PLINTH_CONCURRENCY"), out var c) && c > 0 ? c : null;
        var signing = env("PLINTH_SIGNING_KEY");
        return new PipelineOptions(
            FetchPolicy.FromHostList(env("PLINTH_ALLOWED_HOSTS")) with { MaxBytes = MaxBytesFrom(env("PLINTH_MAX_BYTES")) },
            env("PLINTH_STORE") ?? "none",
            recipes,
            string.IsNullOrEmpty(signing) ? null : signing,
            onFailure,
            concurrency,
            MaxInFlightFrom(env("PLINTH_MAX_INFLIGHT")),
            // Off unless asked for. The upload route takes a gate slot before it can check a
            // signature, so on a public host it is a way to hold the pipeline with no key;
            // see ApiHost. Ingest callers that hold the bytes turn it on deliberately.
            NormalizeEnabled: env("PLINTH_ENABLE_NORMALIZE") == "true",
            MaxPixels: MaxPixelsFrom(env("PLINTH_MAX_PIXELS")));
    }

    /// <summary>
    /// The largest source the engine will decode, in pixels; anything over it is refused at
    /// the header, before a pixel is read. The render decodes at output scale, so this is
    /// not the size of a normal decode — it bounds the worst case, a whole-frame decoder
    /// (webp) on a huge frame, of which <see cref="DefaultMaxInFlight"/> can be in flight.
    /// Roughly: peak ≈ MaxInFlight × (MaxPixels × 4 bytes + MaxBytes) plus the runtime.
    /// </summary>
    private static int MaxPixelsFrom(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return SourceInspector.MaxPixels;
        if (!int.TryParse(raw, out var n) || n < 1)
            throw new PlinthException("PLINTH_MAX_PIXELS must be an integer of 1 or more");
        return n;
    }

    /// <summary>
    /// How many requests may be inside the pipeline at once. Everything beyond that queues
    /// on the gate rather than being rejected, so a burst costs latency, not errors — and
    /// memory stays bounded by this number, not by the number of open connections.
    /// </summary>
    private static int MaxInFlightFrom(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return DefaultMaxInFlight;
        if (!int.TryParse(raw, out var n) || n < 1)
            throw new PlinthException("PLINTH_MAX_INFLIGHT must be an integer of 1 or more");
        return n;
    }

    /// <summary>
    /// The largest source the fetcher will pull, in bytes. It is a memory bound before it is
    /// anything else — <see cref="DefaultMaxInFlight"/> sources of this size can be in flight
    /// at once — so it is deliberately settable per deployment rather than fixed at build
    /// time. A provider that re-hosts 16 MB thumbnails is not a reason to cut a release.
    /// </summary>
    private static int MaxBytesFrom(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return FetchPolicy.DefaultMaxBytes;
        if (!int.TryParse(raw, out var n) || n < 1)
            throw new PlinthException("PLINTH_MAX_BYTES must be an integer of 1 or more");
        return n;
    }

    /// <summary>
    /// A missing or unreadable recipes file should name the path that was tried; the raw I/O
    /// message ("Could not find file '/app/x.json'") buries it in framework wording.
    /// </summary>
    private static string ReadRecipes(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new PlinthException($"PLINTH_RECIPES: could not read {path}");
        }
    }
}
