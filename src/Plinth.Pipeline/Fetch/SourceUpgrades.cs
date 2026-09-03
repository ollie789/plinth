using System.Text.RegularExpressions;

namespace Plinth.Pipeline.Fetch;

/// <summary>
/// Some feeds hand us a thumbnail when the same host will serve the master for
/// the same request. Rewriting the URL before anything else costs nothing and
/// is the difference between carding 154 px of product and carding 2560.
/// Pure and offline: a rewrite is a claim about a host's URL scheme, never a
/// probe, so an upgrade that turns out to 404 fails the fetch like any other
/// bad URL rather than silently falling back.
/// </summary>
public static partial class SourceUpgrades
{
    private const string AdidasFeedWidth = "/w_500,";
    private const string AdidasFullWidth = "/w_1200,";

    /// <summary>
    /// What a size token has to look like, whole. Anchored, because it
    /// validates one already-located substring rather than hunting for one:
    /// searching with this pattern would let a greedy leftmost match swallow
    /// path content ahead of the real token.
    /// </summary>
    [GeneratedRegex(@"^\._[A-Za-z0-9,+_-]*_\.$")]
    private static partial Regex AmazonSizeToken { get; }

    /// <summary>
    /// The upgraded URL for <paramref name="url"/>, or <paramref name="url"/>
    /// itself when its host has no rule or the rule does not apply. Never
    /// throws: anything that is not an absolute URL comes straight back.
    /// Idempotent — applying it twice is applying it once.
    /// </summary>
    public static string Apply(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;

        var path = uri.AbsolutePath;
        var upgraded = uri.Host switch
        {
            // A feed URL asks for 154-679 px; the master is 500-2560, padded on
            // a square white canvas, which is already the shape we want.
            "m.media-amazon.com" => DropAmazonSizeToken(path),
            // The feed asks for w_500; the same path at w_1200 returns 1200 px.
            "assets.adidas.com" => RaiseAdidasWidth(path),
            _ => path,
        };

        return upgraded == path ? url : uri.GetLeftPart(UriPartial.Authority) + upgraded + uri.Query + uri.Fragment;
    }

    /// <summary>
    /// Amazon's size token sits between the id and the extension —
    /// <c>/images/I/&lt;id&gt;._AC_SY445_.jpg</c> — and dropping it returns the
    /// master. Only the last token goes: it runs from the final <c>._</c> to
    /// the <c>_.</c> immediately before the extension, so an id that happens to
    /// carry a dot of its own (<c>71abc.x._AC_SY445_.jpg</c>) keeps it.
    /// </summary>
    private static string DropAmazonSizeToken(string path)
    {
        // The extension is a plain alphanumeric tail, and the token has to end
        // at the "_." immediately before it.
        var dot = path.LastIndexOf('.');
        if (dot < 1 || dot == path.Length - 1 || path[dot - 1] != '_') return path;
        for (var i = dot + 1; i < path.Length; i++)
            if (!char.IsAsciiLetterOrDigit(path[i])) return path;

        var start = path.AsSpan(0, dot).LastIndexOf("._".AsSpan());
        if (start < 0) return path;

        // The candidate spans the last "._" through the extension's dot. The
        // token's character class excludes "/" and ".", so a candidate that
        // reaches back across a path segment is rejected here.
        return AmazonSizeToken.IsMatch(path.AsSpan(start, dot - start + 1))
            ? string.Concat(path.AsSpan(0, start), path.AsSpan(dot))
            : path;
    }

    /// <summary>
    /// The width lives in the first transform segment; anything later in the
    /// path that happens to read the same way is not it.
    /// </summary>
    private static string RaiseAdidasWidth(string path)
    {
        var at = path.IndexOf(AdidasFeedWidth, StringComparison.Ordinal);
        return at < 0 ? path
            : string.Concat(path.AsSpan(0, at), AdidasFullWidth, path.AsSpan(at + AdidasFeedWidth.Length));
    }
}
