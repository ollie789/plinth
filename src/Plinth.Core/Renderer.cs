using NetVips;

namespace Plinth.Core;

public sealed record OutputInfo(int Width, int Height, int Bytes, string Format);

public sealed record Rendered(byte[] Bytes, OutputInfo Info, int DecodeWidth, int DecodeHeight);

/// <summary>
/// Decodes only as many pixels as the content box needs, crops to the
/// measured box, fits it into the content box and centres it on the canvas.
/// </summary>
public static class Renderer
{
    public static (int w, int h) DecodeSizeFor(SourceInfo info, Measurement m, Recipe recipe)
    {
        var (w, h) = info.Orientation is >= 5 and <= 8 ? (info.Height, info.Width) : (info.Width, info.Height);
        var s = Math.Max(recipe.ContentBoxWidth / (double)m.Box.Width, recipe.ContentBoxHeight / (double)m.Box.Height);
        if (s > 1) s = 1;
        return ((int)Math.Ceiling(w * s) + 1, (int)Math.Ceiling(h * s) + 1);
    }

    public static Rendered Render(byte[] source, SourceInfo info, Measurement m, Recipe recipe)
    {
        Engine.Init();
        var (fullW, fullH) = info.Orientation is >= 5 and <= 8 ? (info.Height, info.Width) : (info.Width, info.Height);
        var (dw, dh) = DecodeSizeFor(info, m, recipe);

        Image img;
        try
        {
            img = Image.ThumbnailBuffer(source, dw, height: dh, size: Enums.Size.Down,
                outputProfile: "srgb", intent: Enums.Intent.Relative);
        }
        catch (VipsException e)
        {
            throw new PlinthException("could not decode source", e);
        }
        img = Measurer.MakeOpaqueSrgb(img, recipe.Background);
        var decodeW = img.Width;
        var decodeH = img.Height;

        try
        {
            if (!m.TrimIsNoop)
            {
                // Per-axis scale factors: the decode target can be limited by
                // either axis (whichever needs more pixels to cover the content
                // box), so the other axis lands short of its exact ratio. A
                // single shared scalar drifts on that axis; per-axis scales
                // track each dimension's own rounding.
                var ax = img.Width / (double)fullW;
                var ay = img.Height / (double)fullH;
                var left = Math.Clamp((int)Math.Round(m.Box.Left * ax), 0, img.Width - 1);
                var top = Math.Clamp((int)Math.Round(m.Box.Top * ay), 0, img.Height - 1);
                var width = Math.Clamp((int)Math.Round(m.Box.Width * ax), 1, img.Width - left);
                var height = Math.Clamp((int)Math.Round(m.Box.Height * ay), 1, img.Height - top);
                var crop = img.ExtractArea(left, top, width, height);
                img.Dispose();
                img = crop;
            }

            var f = Math.Min(recipe.ContentBoxWidth / (double)img.Width, recipe.ContentBoxHeight / (double)img.Height);
            if (!recipe.Upscale) f = Math.Min(f, 1);
            if (Math.Abs(f - 1) >= 0.001)
            {
                var resized = img.Resize(f);
                img.Dispose();
                img = resized;
            }

            var canvas = img.Gravity(Enums.CompassDirection.Centre, recipe.CanvasWidth, recipe.CanvasHeight,
                extend: Enums.Extend.Background, background: recipe.Background.ToVips());
            img.Dispose();
            img = canvas;

            var bytes = recipe.Format == "png"
                ? img.PngsaveBuffer(compression: 6, keep: Enums.ForeignKeep.None)
                : img.WebpsaveBuffer(q: recipe.Quality, effort: 4, smartSubsample: false, keep: Enums.ForeignKeep.None);

            return new Rendered(bytes, new OutputInfo(recipe.CanvasWidth, recipe.CanvasHeight, bytes.Length, recipe.Format), decodeW, decodeH);
        }
        catch (VipsException e)
        {
            throw new PlinthException("render failed", e);
        }
        finally
        {
            img.Dispose();
        }
    }
}
