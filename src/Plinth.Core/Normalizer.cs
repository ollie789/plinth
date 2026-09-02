using System.Diagnostics;
using System.Security.Cryptography;

namespace Plinth.Core;

public sealed record NormalizeResult(string Status, byte[]? Output, ResultRecord Record);

/// <summary>
/// The one entry point. Pure and deterministic: same bytes and recipe give
/// byte-identical output and an identical record (timings aside). Never
/// throws for a bad image; the record says what went wrong.
/// </summary>
public static class Normalizer
{
    public static NormalizeResult Normalize(byte[] source, Recipe recipe, string? sourceId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(recipe);
        Engine.Init();

        // One hash of the source: it is both the record's sha256 and, when the
        // caller has no id of its own, the id itself.
        var sha = Convert.ToHexStringLower(SHA256.HashData(source));
        var id = sourceId ?? SourceId.FromSha256(sha);
        var key = OutputKey.Compute(id, recipe);
        var libvips = Engine.LibvipsVersion;
        var total = Stopwatch.StartNew();

        SourceRecord src = ResultRecord.EmptySource with { Sha256 = sha, Bytes = source.Length };
        GroundRecord ground = ResultRecord.EmptyGround;
        TrimRecord trim = ResultRecord.EmptyTrim;
        VerdictRecord verdict = ResultRecord.EmptyVerdict;
        long tInspect = 0, tMeasure = 0, tRender = 0;

        try
        {
            ct.ThrowIfCancellationRequested();

            // Validate at the boundary: a bad recipe becomes a failed record like
            // any other bad input. Validation rounds contentShare, so the key is
            // recomputed from the recipe the pipeline will actually use.
            recipe = recipe.Validated();
            key = OutputKey.Compute(id, recipe);

            var sw = Stopwatch.StartNew();
            var info = SourceInspector.Inspect(source);
            ct.ThrowIfCancellationRequested();
            tInspect = sw.ElapsedMilliseconds;
            src = new SourceRecord(sha, source.Length, info.Width, info.Height, info.Format, info.HasAlpha, info.Orientation);

            sw.Restart();
            var m = Measurer.Measure(source, info, recipe);
            ct.ThrowIfCancellationRequested();
            tMeasure = sw.ElapsedMilliseconds;
            ground = new GroundRecord(m.Ground.Sampled.ToHex(), m.Ground.CornerSpread, m.Ground.CornersAgree,
                VerdictScorer.MatchesBackground(m.Ground.Sampled, recipe));
            trim = new TrimRecord(m.Box.Left, m.Box.Top, m.Box.Width, m.Box.Height, m.TrimIsNoop, Math.Round(m.ContentShareBefore, 4));
            var v = VerdictScorer.Score(m, info, recipe);
            verdict = new VerdictRecord(v.PackShot, v.Confidence, v.Reasons);

            NormalizeResult Passthrough(string reason) => new("passthrough", source,
                new ResultRecord(key, id, Engine.Version, libvips, recipe.Hash, "passthrough", null, reason,
                    src, ground, trim, verdict,
                    new OutputRecord(info.Width, info.Height, source.Length, info.Format),
                    new TimingsRecord(tInspect, tMeasure, 0, 0, 0, total.ElapsedMilliseconds)));

            // The verdict now decides. A scene shrunk onto a white card is worse
            // than the scene untouched, so unless the recipe asks for a card the
            // editorial image is handed back exactly as it arrived — but only
            // when a browser could show those bytes; anything else is carded.
            if (!v.PackShot && recipe.Editorial == "passthrough" && ImageFormats.IsBrowserSafe(info.Format))
                return Passthrough("editorial");
            if (IsPassthrough(info, m, recipe)) return Passthrough("already-normalised");

            sw.Restart();
            // A pack shot whose ground already is the recipe background cards on
            // it; one shot on its own grey cards on that grey, so the extended
            // canvas is seamless rather than a grey box floated on white.
            var canvasBackground = ground.MatchesBackground ? recipe.Background : m.Ground.Sampled;
            var rendered = Renderer.Render(source, info, m, recipe, canvasBackground);
            tRender = sw.ElapsedMilliseconds;

            var output = new OutputRecord(rendered.Info.Width, rendered.Info.Height, rendered.Info.Bytes, rendered.Info.Format);
            var record = new ResultRecord(key, id, Engine.Version, libvips, recipe.Hash, "ok", null, null,
                src, ground, trim, verdict, output,
                new TimingsRecord(tInspect, tMeasure, 0, tRender, 0, total.ElapsedMilliseconds));
            return new NormalizeResult("ok", rendered.Bytes, record);
        }
        catch (Exception e) when (e is PlinthException or NetVips.VipsException)
        {
            var record = new ResultRecord(key, id, Engine.Version, libvips, recipe.Hash, "failed", e.Message, null,
                src, ground, trim, verdict, null,
                new TimingsRecord(tInspect, tMeasure, 0, tRender, 0, total.ElapsedMilliseconds));
            return new NormalizeResult("failed", null, record);
        }
    }

    /// <summary>
    /// True only when re-encoding could not change the bytes in any way that matters:
    /// same format, exactly the canvas size, no alpha, no orientation to apply, no
    /// metadata to strip, the recipe's ground, and the content already at the
    /// recipe's share. Anything less and the source is rendered.
    /// </summary>
    private static bool IsPassthrough(SourceInfo info, Measurement m, Recipe recipe)
    {
        if (info.Format != recipe.Format || info.Width != recipe.CanvasWidth || info.HasAlpha) return false;
        // An output carries no orientation tag and no metadata; a source with either
        // would change on re-encode, so it is not already normalised.
        if (info.Orientation != 1 || info.HasMetadata) return false;
        var (w, h) = info.Orientation is >= 5 and <= 8 ? (info.Height, info.Width) : (info.Width, info.Height);
        var srcAspect = w / (double)h;
        var want = recipe.CanvasWidth / (double)recipe.CanvasHeight;
        if (Math.Abs(srcAspect - want) / want > 0.01) return false;
        if (m.Ground.Sampled.Distance(recipe.Background) > recipe.TrimThreshold) return false;
        return Math.Abs(m.ContentShareBefore - recipe.ContentShare) <= 0.03;
    }
}
