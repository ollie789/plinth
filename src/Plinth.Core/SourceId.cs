using System.Security.Cryptography;

namespace Plinth.Core;

/// <summary>
/// The identity of a source: its canonical URL when fetched, otherwise the
/// hash of its bytes. Chosen so a key can be computed from a URL without
/// downloading anything.
/// </summary>
public static class SourceId
{
    /// <summary>
    /// Returns an RFC 3986 syntax-normalised form of <paramref name="url"/>, computed by
    /// <see cref="System.Uri"/>. Scheme and host are lowercased, the default port (443) is
    /// dropped, and the fragment is dropped. As part of the same normalisation, System.Uri
    /// decodes percent-encoded unreserved characters (e.g. <c>%7E</c> becomes <c>~</c>) and
    /// removes dot-segments (<c>/x/../b.jpg</c> becomes <c>/b.jpg</c>) — this converges
    /// equivalent spellings of the same resource onto one id without ever merging different
    /// resources, since percent-encoded reserved characters (<c>%2F</c>, <c>%3F</c>, <c>%26</c>,
    /// and so on) are left encoded. Query parameter order and path case are otherwise preserved
    /// as given.
    /// </summary>
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
