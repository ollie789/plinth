namespace Plinth.Core;

/// <summary>
/// The formats Plinth accepts, and the names things outside Core need for them.
/// Format strings are Plinth's own stable spelling, not libvips loader names.
/// </summary>
public static class ImageFormats
{
    /// <summary>
    /// Formats every browser can render. An editorial passthrough hands back
    /// the source's own bytes and format, so it is only safe for these; a tiff
    /// or a heif has to be carded whatever the policy says, or the tile would
    /// be something the page cannot show.
    /// </summary>
    public static bool IsBrowserSafe(string format) => format is "jpeg" or "png" or "webp" or "gif";

    /// <summary>Canonical file extension, without the dot.</summary>
    public static string ExtensionFor(string format) => format switch
    {
        "jpeg" => "jpg",
        "png" => "png",
        "webp" => "webp",
        "gif" => "gif",
        "tiff" => "tif",
        "heif" => "heif",
        _ => throw new PlinthException($"unsupported image format: {format}"),
    };

    public static string MimeTypeFor(string format) => format switch
    {
        "jpeg" => "image/jpeg",
        "png" => "image/png",
        "webp" => "image/webp",
        "gif" => "image/gif",
        "tiff" => "image/tiff",
        "heif" => "image/heif",
        _ => throw new PlinthException($"unsupported image format: {format}"),
    };
}
