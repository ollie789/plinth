using System.Security.Cryptography;

namespace Plinth.Core;

/// <summary>
/// The identity of a source: its canonical URL when fetched, otherwise the
/// hash of its bytes. Chosen so a key can be computed from a URL without
/// downloading anything.
/// </summary>
public static class SourceId
{
    public static string FromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
            throw new PlinthException($"source must be an absolute https URL, got '{url}'");
        var b = new UriBuilder(u) { Fragment = "", Scheme = "https" };
        b.Host = b.Host.ToLowerInvariant();
        if (b.Port == 443) b.Port = -1;
        return b.Uri.GetComponents(
            UriComponents.Scheme | UriComponents.Host | UriComponents.Port | UriComponents.PathAndQuery,
            UriFormat.UriEscaped);
    }

    public static string FromBytes(ReadOnlySpan<byte> bytes) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
}
