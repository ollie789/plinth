using NetVips;

namespace Plinth.Core;

public sealed record GroundInfo(Rgb Sampled, int CornerSpread, bool CornersAgree);

/// <summary>Coordinates are in orientation-corrected (display) space, matching what <c>ThumbnailBuffer</c> produces.</summary>
public readonly record struct Box(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;

    public int TouchesEdges(int frameWidth, int frameHeight) =>
        (Left == 0 ? 1 : 0) + (Top == 0 ? 1 : 0)
        + (Right >= frameWidth ? 1 : 0) + (Bottom >= frameHeight ? 1 : 0);
}

/// <summary><see cref="Box"/> is in source pixel coordinates, orientation-corrected (display) space.</summary>
public sealed record Measurement(
    GroundInfo Ground,
    Box Box,
    bool TrimIsNoop,
    double ContentShareBefore,
    int MeasureWidth,
    int MeasureHeight);

/// <summary>
/// Samples the ground and finds the content box on a small working copy,
/// then maps the box back to source coordinates. Measuring small is about
/// ten times cheaper than trimming at full size and gives the same box.
/// </summary>
public static class Measurer
{
    public const int MeasureSide = 512;
    private const int Patch = 8;

    public static Measurement Measure(byte[] bytes, SourceInfo info, Recipe recipe)
    {
        Engine.Init();
        using var thumb = LoadWorkingCopy(bytes, recipe, MeasureSide);

        var ground = SampleGround(thumb, recipe.TrimThreshold);

        var (fullW, fullH) = info.Orientation is >= 5 and <= 8 ? (info.Height, info.Width) : (info.Width, info.Height);
        // Independent per-axis scales: the thumbnail is fit within MeasureSide x MeasureSide,
        // so whichever axis is the limiting one rounds to an exact ratio while the other axis
        // carries the rounding error. A single shared scale drifts on that non-limiting axis
        // (worse for more extreme aspect ratios); per-axis scales don't.
        var scaleX = thumb.Width / (double)fullW;
        var scaleY = thumb.Height / (double)fullH;

        var raw = thumb.FindTrim(threshold: recipe.TrimThreshold, background: ground.Sampled.ToVips());
        var t = raw.Select(Convert.ToInt32).ToArray();
        var noop = t[2] == 0 || t[3] == 0 || (t[0] == 0 && t[1] == 0 && t[2] == thumb.Width && t[3] == thumb.Height);

        Box box;
        if (noop)
        {
            box = new Box(0, 0, fullW, fullH);
        }
        else
        {
            var left = Math.Clamp((int)Math.Floor(t[0] / scaleX), 0, fullW - 1);
            var top = Math.Clamp((int)Math.Floor(t[1] / scaleY), 0, fullH - 1);
            var right = Math.Clamp((int)Math.Ceiling((t[0] + t[2]) / scaleX), left + 1, fullW);
            var bottom = Math.Clamp((int)Math.Ceiling((t[1] + t[3]) / scaleY), top + 1, fullH);
            box = new Box(left, top, right - left, bottom - top);
        }

        var share = Math.Max(box.Width / (double)fullW, box.Height / (double)fullH);
        return new Measurement(ground, box, noop, share, thumb.Width, thumb.Height);
    }

    /// <summary>Orientation-applied, sRGB, opaque, 3-band working copy no larger than <paramref name="side"/>.</summary>
    internal static Image LoadWorkingCopy(byte[] bytes, Recipe recipe, int side)
    {
        Image img;
        try
        {
            img = Image.ThumbnailBuffer(bytes, side, height: side, size: Enums.Size.Down,
                outputProfile: "srgb", intent: Enums.Intent.Relative);
        }
        catch (VipsException e)
        {
            throw new PlinthException("could not decode source", e);
        }
        using var opaque = MakeOpaqueSrgb(img, recipe.Background);
        // The thumbnail pipeline is sequential (single forward pass over the
        // decoder). We then read overlapping regions of it repeatedly - four
        // corner patches plus a full-frame trim scan - so it must be forced
        // into memory first for random access, or the second read fails.
        return opaque.CopyMemory();
    }

    internal static Image MakeOpaqueSrgb(Image img, Rgb background)
    {
        if (img.HasAlpha())
        {
            var flat = img.Flatten(background: background.ToVips());
            img.Dispose();
            img = flat;
        }
        if (img.Bands < 3)
        {
            var rgb = img.Colourspace(Enums.Interpretation.Srgb);
            img.Dispose();
            img = rgb;
        }
        else if (img.Bands > 3)
        {
            var rgb = img.ExtractBand(0, n: 3);
            img.Dispose();
            img = rgb;
        }
        return img;
    }

    private static GroundInfo SampleGround(Image img, int threshold)
    {
        var p = Math.Min(Patch, Math.Min(img.Width, img.Height));
        var corners = new[]
        {
            PatchMean(img, 0, 0, p),
            PatchMean(img, img.Width - p, 0, p),
            PatchMean(img, 0, img.Height - p, p),
            PatchMean(img, img.Width - p, img.Height - p, p),
        };
        var ground = new Rgb(Median(corners.Select(c => c.R)), Median(corners.Select(c => c.G)), Median(corners.Select(c => c.B)));
        var spread = corners.Max(c => c.Distance(ground));
        return new GroundInfo(ground, spread, spread <= threshold);
    }

    private static Rgb PatchMean(Image img, int x, int y, int p)
    {
        using var patch = img.ExtractArea(x, y, p, p);
        using var stats = patch.Stats();
        // stats: one row per band after row 0 (whole image); column 4 is the mean.
        return Rgb.FromDoubles(stats.Getpoint(4, 1)[0], stats.Getpoint(4, 2)[0], stats.Getpoint(4, 3)[0]);
    }

    private static byte Median(IEnumerable<byte> values)
    {
        var v = values.OrderBy(x => x).ToArray();
        return (byte)((v[1] + v[2]) / 2);
    }
}
