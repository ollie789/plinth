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
    /// <summary>
    /// Amazon's size token sits between the id and the extension —
    /// <c>/images/I/&lt;id&gt;._AC_SY445_.jpg</c> — and dropping it returns the
    /// master. The token's own separators are underscores, so they are part of
    /// what the class has to match.
    /// </summary>
    [GeneratedRegex(@"\._[A-Za-z0-9,+_-]*_\.(?=[A-Za-z0-9]+$)")]
    private static partial Regex AmazonSizeToken { get; }

    /// <summary>
    /// The upgraded URL for <paramref name="url"/>, or <paramref name="url"/>
    /// itself when its host has no rule or the rule does not apply. Never
    /// throws: anything that is not an absolute URL comes straight back.
    /// </summary>
    public static string Apply(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;

        var path = uri.AbsolutePath;
        var upgraded = uri.Host switch
        {
            // A feed URL asks for 154-679 px; the master is 500-2560, padded on
            // a square white canvas, which is already the shape we want.
            "m.media-amazon.com" => AmazonSizeToken.Replace(path, "."),
            // The feed asks for w_500; the same path at w_1200 returns 1200 px.
            "assets.adidas.com" => path.Replace("/w_500,", "/w_1200,", StringComparison.Ordinal),
            _ => path,
        };

        return ReferenceEquals(upgraded, path) || upgraded == path
            ? url
            : uri.GetLeftPart(UriPartial.Authority) + upgraded + uri.Query + uri.Fragment;
    }
}
