using NetVips;

namespace Plinth.Core;

/// <summary><paramref name="HasMetadata"/> is true when the source carries EXIF, XMP, IPTC or an ICC profile.</summary>
public sealed record SourceInfo(string Format, int Width, int Height, bool HasAlpha, int Pages, int Orientation,
    bool HasMetadata = false);

/// <summary>Header facts about a source, read without decoding pixels.</summary>
public static class SourceInspector
{
    /// <summary>Refuse anything larger than this before decoding (decompression bombs).</summary>
    public const int MaxPixels = 30_000_000;

    public static SourceInfo Inspect(byte[] bytes, int maxPixels = MaxPixels)
    {
        Engine.Init();
        var loader = Image.FindLoadBuffer(bytes)
            ?? throw new PlinthException("not a supported image");
        var format = LoaderToFormat(loader);

        Image img;
        try
        {
            // Loading is lazy: libvips reads the header only until pixels are asked for.
            img = Image.NewFromBuffer(bytes, access: Enums.Access.Sequential);
        }
        catch (VipsException e)
        {
            throw new PlinthException("not a supported image", e);
        }
        using (img)
        {
            var pages = img.GetTypeOf("n-pages") != IntPtr.Zero && img.Get("n-pages") is int n ? n : 1;
            var orientation = img.GetTypeOf("orientation") != IntPtr.Zero && img.Get("orientation") is int o ? o : 1;
            var width = img.Width;
            var height = pages > 1 && img.GetTypeOf("page-height") != IntPtr.Zero && img.Get("page-height") is int ph
                ? ph : img.Height;
            if ((long)width * height > maxPixels)
                throw new PlinthException($"source too large: {width}x{height} exceeds {maxPixels} pixels");
            return new SourceInfo(format, width, height, img.HasAlpha(), pages, orientation, HasMetadata(img));
        }
    }

    /// <summary>Any of the metadata blocks a normalised output would have stripped.</summary>
    private static bool HasMetadata(Image img) =>
        MetadataFields.Any(name => img.GetTypeOf(name) != IntPtr.Zero);

    private static readonly string[] MetadataFields = ["exif-data", "xmp-data", "icc-profile-data", "iptc-data"];

    /// <summary>
    /// An allowlist, not a transformation: libvips loads far more than Plinth is
    /// willing to decode (svg, pdf, magick, ...), and its loader names are not
    /// format names ("nsgif"). Mapping explicitly keeps both under control.
    /// </summary>
    private static readonly Dictionary<string, string> Formats = new(StringComparer.Ordinal)
    {
        ["VipsForeignLoadJpegBuffer"] = "jpeg",
        ["VipsForeignLoadPngBuffer"] = "png",
        ["VipsForeignLoadWebpBuffer"] = "webp",
        ["VipsForeignLoadNsgifBuffer"] = "gif",
        ["VipsForeignLoadGifBuffer"] = "gif",
        ["VipsForeignLoadTiffBuffer"] = "tiff",
        ["VipsForeignLoadHeifBuffer"] = "heif",
    };

    private static string LoaderToFormat(string loader) =>
        Formats.TryGetValue(loader, out var format)
            ? format
            : throw new PlinthException($"unsupported image format: {loader}");
}
