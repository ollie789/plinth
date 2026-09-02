namespace Plinth.Core;

public sealed record Verdict(bool PackShot, double Confidence, IReadOnlyList<string> Reasons);

/// <summary>
/// Is this a flat-ground pack shot that is safe to trim onto a card? Reported
/// on every image so the heuristic can be tuned on real traffic; in v1 the
/// host allowlist, not this score, decides what gets trimmed.
/// </summary>
public static class VerdictScorer
{
    public static Verdict Score(Measurement m, SourceInfo info, Recipe recipe)
    {
        var (w, h) = info.Orientation is >= 5 and <= 8 ? (info.Height, info.Width) : (info.Width, info.Height);
        var reasons = new List<string>();
        var score = 1.0;

        void Fail(string reason, double penalty) { reasons.Add(reason); score -= penalty; }

        if (!m.Ground.CornersAgree) Fail("corners-disagree", 0.4);
        if (m.Ground.Sampled.Distance(recipe.Background) > 40) Fail("ground-not-background", 0.2);
        if (m.Box.TouchesEdges(w, h) >= 2) Fail("touches-edges", 0.3);
        if (m.ContentShareBefore < 0.05) Fail("content-tiny", 0.3);
        else if (m.ContentShareBefore > 0.98) Fail("content-fills-frame", 0.3);
        var aspect = m.Box.Width / (double)Math.Max(1, m.Box.Height);
        if (aspect > 6 || aspect < 1.0 / 6) Fail("thin-strip", 0.2);

        var confidence = Math.Round(Math.Clamp(score, 0, 1), 2);
        return new Verdict(confidence >= 0.5, confidence, reasons);
    }
}
