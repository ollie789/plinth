using Plinth.Core;

namespace Plinth.Tests;

public class VerdictScorerTests
{
    private static readonly SourceInfo Info = new("jpeg", 1000, 1000, false, 1, 1);
    private static Measurement M(GroundInfo g, Box b, double share) => new(g, b, false, share, 512, 512);
    private static readonly GroundInfo Clean = new(Rgb.Parse("#ffffff"), 2, true);

    [Fact]
    public void A_clean_centred_pack_shot_scores_full_confidence()
    {
        var v = VerdictScorer.Score(M(Clean, new Box(200, 200, 600, 600), 0.6), Info, Recipe.Default);
        Assert.True(v.PackShot);
        Assert.Equal(1.0, v.Confidence);
        Assert.Empty(v.Reasons);
    }

    [Fact]
    public void Disagreeing_corners_and_edge_contact_fail_the_verdict()
    {
        var busy = new GroundInfo(Rgb.Parse("#808080"), 90, false);
        var v = VerdictScorer.Score(M(busy, new Box(0, 0, 1000, 700), 1.0), Info, Recipe.Default);
        Assert.False(v.PackShot);
        Assert.Contains("corners-disagree", v.Reasons);
        Assert.Contains("ground-not-background", v.Reasons);
        Assert.Contains("touches-edges", v.Reasons);
        Assert.Contains("content-fills-frame", v.Reasons);
        Assert.Equal(0.0, v.Confidence);
    }

    [Fact]
    public void Tiny_content_and_thin_strips_lose_points_but_may_still_pass()
    {
        var v = VerdictScorer.Score(M(Clean, new Box(480, 100, 40, 800), 0.8), Info, Recipe.Default);
        Assert.Contains("thin-strip", v.Reasons);
        Assert.Equal(0.8, v.Confidence);
        var tiny = VerdictScorer.Score(M(Clean, new Box(490, 490, 20, 20), 0.02), Info, Recipe.Default);
        Assert.Contains("content-tiny", tiny.Reasons);
        Assert.Equal(0.7, tiny.Confidence);
    }
}
