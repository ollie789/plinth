namespace Plinth.Core;

public sealed record Verdict(bool PackShot, double Confidence, IReadOnlyList<string> Reasons);

/// <summary>
/// Is this a flat-ground pack shot, or an editorial image — a model on a
/// backdrop, a room, a rug in a lounge — that carding would ruin? The score
/// runs from 1.0 down; at 0.5 and above the image is a pack shot, and below it
/// the recipe's <c>editorial</c> policy decides what happens.
/// </summary>
public static class VerdictScorer
{
    /// <summary>
    /// How far the sampled ground may sit from the recipe background and still
    /// count as it. Over a 2,074-image live run the flagged scenes' grounds sat
    /// 104–147 from white at the quartiles; every good pack shot's sat within 5.
    /// </summary>
    public const int BackgroundTolerance = 40;

    /// <summary>
    /// Is the sampled ground the recipe's own background, within
    /// <see cref="BackgroundTolerance"/>? The single definition of that test:
    /// the verdict gates its framing checks on it, and the renderer picks the
    /// canvas colour by it.
    /// </summary>
    public static bool MatchesBackground(Rgb ground, Recipe recipe) =>
        ground.Distance(recipe.Background) <= BackgroundTolerance;

    public static Verdict Score(Measurement m, SourceInfo info, Recipe recipe)
    {
        var (w, h) = info.Orientation is >= 5 and <= 8 ? (info.Height, info.Width) : (info.Width, info.Height);
        var reasons = new List<string>();
        var score = 1.0;

        void Fail(string reason, double penalty) { reasons.Add(reason); score -= penalty; }

        // The ground is the strongest single signal, so it carries the largest
        // penalty and it gates the two framing tests below. It stops short of
        // 0.5 on its own: a clean pack shot on its own grey ground has to keep
        // a margin over the line, and it takes a second failure to cross it.
        var foreignGround = !MatchesBackground(m.Ground.Sampled, recipe);
        if (foreignGround) Fail("ground-not-background", 0.4);

        // Studio lighting gradients spread the corners a little on perfectly
        // good pack shots, so the corners get slack: at least 24, and more when
        // the recipe's own trim tolerance is looser than that.
        if (m.Ground.CornerSpread > Math.Max(24, recipe.TrimThreshold * 2)) Fail("corners-disagree", 0.3);

        // A tight product crop that runs to the frame edge is how Amazon and
        // Myer ship a pack shot. Edge contact and a full frame are evidence of
        // a scene only for an image that is not already on the recipe's ground.
        if (foreignGround)
        {
            if (m.Box.TouchesEdges(w, h) >= 2) Fail("touches-edges", 0.2);
            if (m.ContentShareBefore > 0.98) Fail("content-fills-frame", 0.2);
        }

        if (m.ContentShareBefore < 0.05) Fail("content-tiny", 0.3);
        var aspect = m.Box.Width / (double)Math.Max(1, m.Box.Height);
        if (aspect > 6 || aspect < 1.0 / 6) Fail("thin-strip", 0.2);

        var confidence = Math.Round(Math.Clamp(score, 0, 1), 2);
        return new Verdict(confidence >= 0.5, confidence, reasons);
    }
}
