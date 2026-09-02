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
    public void A_model_on_a_grey_backdrop_filling_the_frame_is_not_a_pack_shot()
    {
        // The Puma shape: a person on a studio backdrop, every edge touched.
        var backdrop = new GroundInfo(Rgb.Parse("#c8c8c8"), 27, false);
        var v = VerdictScorer.Score(M(backdrop, new Box(0, 0, 1000, 1000), 1.0), Info, Recipe.Default);
        Assert.False(v.PackShot);
        Assert.Equal(0.0, v.Confidence);
        Assert.Equal(["ground-not-background", "corners-disagree", "touches-edges", "content-fills-frame"], v.Reasons);
    }

    [Fact]
    public void A_tight_crop_on_the_recipe_ground_stays_a_pack_shot()
    {
        // The Amazon and Myer shape: white ground, content out to the edges.
        // Only the corner spread costs anything; framing is not held against it.
        var lit = new GroundInfo(Rgb.Parse("#ffffff"), 33, false);
        var v = VerdictScorer.Score(M(lit, new Box(0, 0, 990, 990), 0.99), Info, Recipe.Default);
        Assert.True(v.PackShot);
        Assert.Equal(0.7, v.Confidence);
        Assert.Equal(["corners-disagree"], v.Reasons);
    }

    [Fact]
    public void A_pack_shot_on_its_own_grey_ground_still_passes()
    {
        var grey = new GroundInfo(Rgb.Parse("#808080"), 4, true);
        var v = VerdictScorer.Score(M(grey, new Box(200, 200, 600, 600), 0.6), Info, Recipe.Default);
        Assert.True(v.PackShot);
        Assert.Equal(0.5, v.Confidence);
        Assert.Equal(["ground-not-background"], v.Reasons);
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
