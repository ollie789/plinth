namespace Plinth.Core;

/// <summary>
/// The formats Plinth accepts, and the names things outside Core need for them.
/// Format strings are Plinth's own stable spelling, not libvips loader names.
/// </summary>
public static class ImageFormats
{
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
