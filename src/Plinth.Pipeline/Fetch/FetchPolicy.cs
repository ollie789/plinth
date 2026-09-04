namespace Plinth.Pipeline.Fetch;

/// <summary>What the fetcher may touch. The allowlist IS the security model (spec 5.1).</summary>
public sealed record FetchPolicy(
    IReadOnlySet<string> AllowedHosts,
    int MaxBytes = 20 * 1024 * 1024,
    int TimeoutSeconds = 12,
    int MaxRedirects = 3,
    string UserAgent = "Mozilla/5.0 (compatible; Plinth/1.0)")
{
    public static FetchPolicy FromHostList(string? commaList) =>
        new(commaList is null
            ? new HashSet<string>()
            : commaList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(h => h.ToLowerInvariant()).ToHashSet());

    public bool Allows(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && AllowedHosts.Contains(uri.Host.ToLowerInvariant());
}
