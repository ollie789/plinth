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
    /// <summary>
    /// Content already fills the frame; nothing to normalise. A source whose
    /// content share reaches this before any trim gains nothing from the
    /// canvas — carding it only shrinks the product and adds a margin — so it
    /// is handed back untouched unless the recipe asks for a card.
    /// </summary>
    public const double FramedFill = 0.90;

    /// <summary>
    /// How much wider than the canvas a framed source may be and still be
    /// handed back untouched.
    /// <para>
    /// A consumer fits the tile it is given, and crops whatever does not fit.
    /// A source at the canvas aspect survives that fit; one materially wider
    /// loses its ends to it. A shoe shot 2.2 times wider than it is tall,
    /// dropped into a 4:5 tile, shows its middle third and neither toe nor
    /// heel — the frame was the product's own, and the crop took it anyway.
    /// </para>
    /// <para>
    /// Only the wide side is bounded. A source TALLER than the canvas loses a
    /// little off the top and bottom and stays legible, and bounding that side
    /// too would card the portrait model shots this rule exists to protect.
    /// </para>
    /// </summary>
    public const double FramedAspectSlack = 0.05;

    /// <summary>
    /// A ground this close to the recipe background is already the background;
    /// balancing it would be arithmetic with no visible effect. Above it, and
    /// up to <see cref="VerdictScorer.BackgroundTolerance"/>, the ground is
    /// scaled onto the background before the card is made.
    /// </summary>
    public const int GroundBalanceMinDistance = 2;

    /// <summary>
    /// Balancing is a near-white technique, and these are the caps that say so.
    /// The distance to the background is a Chebyshev distance, which bounds how
    /// far a channel moves but not the ratio it moves by: against a dark
    /// background a channel near zero is 40 levels away and still a fortyfold
    /// multiplier. A scale outside this range is not a tint being corrected, so
    /// the image is left alone and cards on the background as it always did.
    /// </summary>
    public const double GroundBalanceMinScale = 0.80;

    /// <inheritdoc cref="GroundBalanceMinScale"/>
    public const double GroundBalanceMaxScale = 1.25;

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
                VerdictScorer.MatchesBackground(m.Ground.Sampled, recipe), Balanced: false);
            trim = new TrimRecord(m.Box.Left, m.Box.Top, m.Box.Width, m.Box.Height, m.TrimIsNoop, Math.Round(m.ContentShareBefore, 4));
            var v = VerdictScorer.Score(m, info, recipe);
            verdict = new VerdictRecord(v.PackShot, v.Confidence, v.Reasons);

            NormalizeResult Passthrough(string reason) => new("passthrough", source,
                new ResultRecord(key, id, Engine.Version, libvips, recipe.Hash, "passthrough", null, reason,
                    src, ground, trim, verdict,
                    new OutputRecord(info.Width, info.Height, source.Length, info.Format),
                    new TimingsRecord(tInspect, tMeasure, 0, 0, 0, total.ElapsedMilliseconds)));

            // Handing back the source only works when a browser could show
            // those bytes; anything else is carded whatever the policy says.
            var mayReturnSource = recipe.Editorial == "passthrough" && ImageFormats.IsBrowserSafe(info.Format);

            // The verdict decides first. A scene shrunk onto a white card is
            // worse than the scene untouched, so an editorial image is handed
            // back exactly as it arrived.
            if (mayReturnSource && !v.PackShot) return Passthrough("editorial");
            // Then the narrower reason, where re-encoding could not change the
            // bytes in any way that matters.
            if (IsPassthrough(info, m, recipe)) return Passthrough("already-normalised");
            // Then the pack shot that is already framed, where the canvas has
            // nothing to add and carding would only shrink the product behind a
            // margin. It has to be framed on both axes and in a frame the
            // consumer's tile will not crop; see IsFramed.
            if (mayReturnSource && IsFramed(info, m, recipe)) return Passthrough("framed");

            sw.Restart();
            // A pack shot whose ground already is the recipe background cards on
            // it; one shot on its own grey cards on that grey, so the extended
            // canvas is seamless rather than a grey box floated on white.
            var canvasBackground = ground.MatchesBackground ? recipe.Background : m.Ground.Sampled;
            var groundScale = GroundScaleFor(v, m, recipe);
            if (groundScale is not null) ground = ground with { Balanced = true };
            var rendered = Renderer.Render(source, info, m, recipe, canvasBackground, groundScale);
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
    /// The per-channel multiplier that maps the sampled ground onto the recipe
    /// background, or null to leave the image alone.
    /// <para>
    /// Off-white studio grounds — #f0f0f0, #ededed, #e1e1e1 — are close enough
    /// to white to card on it, but the trimmed box carries that tint with it,
    /// so the tile shows a hard-edged tinted rectangle around the product's
    /// silhouette. Scaling each channel so the ground lands exactly on the
    /// background removes the rectangle; shadows survive, because they scale by
    /// the same factor the backdrop does.
    /// </para>
    /// <para>
    /// Only for a pack shot on a ground the card is being made of: a ground
    /// beyond <see cref="VerdictScorer.BackgroundTolerance"/> cards on itself
    /// and has nothing to be balanced towards, and one within
    /// <see cref="GroundBalanceMinDistance"/> is already there. A scale outside
    /// <see cref="GroundBalanceMinScale"/>..<see cref="GroundBalanceMaxScale"/>
    /// on any channel is refused outright.
    /// </para>
    /// </summary>
    private static double[]? GroundScaleFor(Verdict v, Measurement m, Recipe recipe)
    {
        if (!v.PackShot) return null;
        var sampled = m.Ground.Sampled;
        var distance = sampled.Distance(recipe.Background);
        if (distance <= GroundBalanceMinDistance || distance > VerdictScorer.BackgroundTolerance) return null;

        var background = recipe.Background;
        double[] scale = [Ratio(background.R, sampled.R), Ratio(background.G, sampled.G), Ratio(background.B, sampled.B)];
        return Array.Exists(scale, c => c < GroundBalanceMinScale || c > GroundBalanceMaxScale) ? null : scale;

        // A black channel carries no ratio to scale by; leaving it alone is the
        // only answer that cannot blow up.
        static double Ratio(byte background, byte ground) => ground == 0 ? 1 : background / (double)ground;
    }

    /// <summary>
    /// True when the source is already the tile it would be carded into, so
    /// handing it back costs the consumer nothing.
    /// <para>
    /// Fill is measured on BOTH axes, not the wider one.
    /// <see cref="Measurement.ContentShareBefore"/> takes the larger axis,
    /// which is the right question for "is there air to trim" and the wrong
    /// one for "is this framed": a frypan lying across a square frame fills
    /// 95% of the width and a fifth of the height, and handing that back
    /// leaves the tile to crop its handle off. The frame is the product's own
    /// only when the product reaches both edges of it.
    /// </para>
    /// <para>
    /// The frame must also be no wider than the canvas allows — see
    /// <see cref="FramedAspectSlack"/>.
    /// </para>
    /// </summary>
    private static bool IsFramed(SourceInfo info, Measurement m, Recipe recipe)
    {
        var (w, h) = DisplaySize(info);
        if (Math.Min(m.Box.Width / (double)w, m.Box.Height / (double)h) < FramedFill) return false;
        var canvasAspect = recipe.CanvasWidth / (double)recipe.CanvasHeight;
        return w / (double)h <= canvasAspect * (1 + FramedAspectSlack);
    }

    /// <summary>Source dimensions as they are displayed, with orientation applied.</summary>
    private static (int Width, int Height) DisplaySize(SourceInfo info) =>
        info.Orientation is >= 5 and <= 8 ? (info.Height, info.Width) : (info.Width, info.Height);

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
        var (w, h) = DisplaySize(info);
        var srcAspect = w / (double)h;
        var want = recipe.CanvasWidth / (double)recipe.CanvasHeight;
        if (Math.Abs(srcAspect - want) / want > 0.01) return false;
        if (m.Ground.Sampled.Distance(recipe.Background) > recipe.TrimThreshold) return false;
        return Math.Abs(m.ContentShareBefore - recipe.ContentShare) <= 0.03;
    }
}
