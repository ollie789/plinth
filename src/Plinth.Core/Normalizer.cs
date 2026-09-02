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
    public static NormalizeResult Normalize(byte[] source, Recipe recipe, string? sourceId = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(recipe);
        Engine.Init();

        var id = sourceId ?? SourceId.FromBytes(source);
        var key = OutputKey.Compute(id, recipe);
        var sha = Convert.ToHexStringLower(SHA256.HashData(source));
        var total = Stopwatch.StartNew();

        SourceRecord src = ResultRecord.EmptySource with { Sha256 = sha, Bytes = source.Length };
        GroundRecord ground = ResultRecord.EmptyGround;
        TrimRecord trim = ResultRecord.EmptyTrim;
        VerdictRecord verdict = ResultRecord.EmptyVerdict;
        long tInspect = 0, tMeasure = 0, tRender = 0;

        try
        {
            var sw = Stopwatch.StartNew();
            var info = SourceInspector.Inspect(source);
            tInspect = sw.ElapsedMilliseconds;
            src = new SourceRecord(sha, source.Length, info.Width, info.Height, info.Format, info.HasAlpha, info.Orientation);

            sw.Restart();
            var m = Measurer.Measure(source, info, recipe);
            tMeasure = sw.ElapsedMilliseconds;
            ground = new GroundRecord(m.Ground.Sampled.ToHex(), m.Ground.CornerSpread, m.Ground.CornersAgree);
            trim = new TrimRecord(m.Box.Left, m.Box.Top, m.Box.Width, m.Box.Height, m.TrimIsNoop, Math.Round(m.ContentShareBefore, 4));
            var v = VerdictScorer.Score(m, info, recipe);
            verdict = new VerdictRecord(v.PackShot, v.Confidence, v.Reasons);

            if (IsPassthrough(info, m, recipe))
            {
                var passRecord = new ResultRecord(key, id, Engine.Version, recipe.Hash, "passthrough", null,
                    src, ground, trim, verdict,
                    new OutputRecord(info.Width, info.Height, source.Length, info.Format),
                    new TimingsRecord(tInspect, tMeasure, 0, 0, 0, total.ElapsedMilliseconds));
                return new NormalizeResult("passthrough", source, passRecord);
            }

            sw.Restart();
            var rendered = Renderer.Render(source, info, m, recipe);
            tRender = sw.ElapsedMilliseconds;

            var output = new OutputRecord(rendered.Info.Width, rendered.Info.Height, rendered.Info.Bytes, rendered.Info.Format);
            var record = new ResultRecord(key, id, Engine.Version, recipe.Hash, "ok", null,
                src, ground, trim, verdict, output,
                new TimingsRecord(tInspect, tMeasure, 0, tRender, 0, total.ElapsedMilliseconds));
            return new NormalizeResult("ok", rendered.Bytes, record);
        }
        catch (Exception e) when (e is PlinthException or NetVips.VipsException)
        {
            var record = new ResultRecord(key, id, Engine.Version, recipe.Hash, "failed", e.Message,
                src, ground, trim, verdict, null,
                new TimingsRecord(tInspect, tMeasure, 0, tRender, 0, total.ElapsedMilliseconds));
            return new NormalizeResult("failed", null, record);
        }
    }

    private static bool IsPassthrough(SourceInfo info, Measurement m, Recipe recipe)
    {
        if (info.Format != recipe.Format || info.Width > recipe.CanvasWidth || info.HasAlpha) return false;
        var srcAspect = info.Width / (double)info.Height;
        var want = recipe.CanvasWidth / (double)recipe.CanvasHeight;
        if (Math.Abs(srcAspect - want) / want > 0.01) return false;
        if (m.Ground.Sampled.Distance(recipe.Background) > recipe.TrimThreshold) return false;
        return Math.Abs(m.ContentShareBefore - recipe.ContentShare) <= 0.03;
    }
}
