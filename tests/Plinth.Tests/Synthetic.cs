using NetVips;
using Plinth.Core;

namespace Plinth.Tests;

/// <summary>Draws deterministic test images so tests never depend on fixtures.</summary>
public static class Synthetic
{
    public static byte[] PackShot(int w, int h, Rgb ground, int boxLeft, int boxTop, int boxW, int boxH,
        Rgb boxColour, string format = "jpeg", int quality = 92)
    {
        using var bg = Image.Black(w, h, bands: 3) + ground.ToVips();
        using var box = Image.Black(boxW, boxH, bands: 3) + boxColour.ToVips();
        using var img = bg.Insert(box, boxLeft, boxTop).Cast(Enums.BandFormat.Uchar)
            .Copy(interpretation: Enums.Interpretation.Srgb);
        return format switch
        {
            "jpeg" => img.JpegsaveBuffer(q: quality),
            "png" => img.PngsaveBuffer(),
            "webp" => img.WebpsaveBuffer(q: quality),
            "tiff" => img.TiffsaveBuffer(),
            _ => throw new ArgumentException(format),
        };
    }

    /// <summary>A box on a fully transparent ground, as PNG.</summary>
    public static byte[] TransparentPackShot(int w, int h, int boxLeft, int boxTop, int boxW, int boxH, Rgb boxColour)
    {
        using var alpha = Image.Black(w, h, bands: 1);
        using var boxAlpha = Image.Black(boxW, boxH, bands: 1) + 255.0;
        using var a = alpha.Insert(boxAlpha, boxLeft, boxTop);
        using var rgb = Image.Black(w, h, bands: 3);
        using var box = Image.Black(boxW, boxH, bands: 3) + boxColour.ToVips();
        using var colour = rgb.Insert(box, boxLeft, boxTop);
        using var img = colour.Bandjoin(a).Cast(Enums.BandFormat.Uchar)
            .Copy(interpretation: Enums.Interpretation.Srgb);
        return img.PngsaveBuffer();
    }

    /// <summary>A plain image of one colour (no product).</summary>
    public static byte[] Flat(int w, int h, Rgb colour, string format = "jpeg")
    {
        using var img = (Image.Black(w, h, bands: 3) + colour.ToVips()).Cast(Enums.BandFormat.Uchar)
            .Copy(interpretation: Enums.Interpretation.Srgb);
        return format == "png" ? img.PngsaveBuffer() : img.JpegsaveBuffer(q: 92);
    }
}
