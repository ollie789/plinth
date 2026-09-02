using Plinth.Core;

namespace Plinth.Tests;

public class MeasurerTests
{
    private static readonly Rgb White = Rgb.Parse("#ffffff");
    private static readonly Rgb Black = Rgb.Parse("#000000");
    private static readonly Rgb Grey = Rgb.Parse("#c8c8c8");

    private static Measurement Measure(byte[] bytes, Recipe? recipe = null) =>
        Measurer.Measure(bytes, SourceInspector.Inspect(bytes), recipe ?? Recipe.Default);

    [Fact]
    public void Finds_the_box_on_a_white_ground_within_a_few_source_pixels()
    {
        var m = Measure(Synthetic.PackShot(800, 1000, White, 200, 300, 300, 400, Black));
        Assert.InRange(m.Box.Left, 194, 202);
        Assert.InRange(m.Box.Top, 294, 302);
        Assert.InRange(m.Box.Width, 298, 312);
        Assert.InRange(m.Box.Height, 398, 412);
        Assert.False(m.TrimIsNoop);
        Assert.Equal(255, m.Ground.Sampled.R);
        Assert.True(m.Ground.CornersAgree);
        Assert.InRange(m.ContentShareBefore, 0.39, 0.42);
    }

    [Fact]
    public void Samples_a_non_white_ground_and_still_trims()
    {
        var m = Measure(Synthetic.PackShot(600, 600, Grey, 100, 100, 200, 200, Black));
        Assert.InRange(m.Ground.Sampled.R, 196, 204);
        Assert.InRange(m.Box.Width, 196, 212);
        Assert.True(m.Ground.CornersAgree);
    }

    [Fact]
    public void Flattens_transparency_onto_the_recipe_background_before_measuring()
    {
        var m = Measure(Synthetic.TransparentPackShot(400, 500, 100, 150, 100, 200, Black));
        Assert.InRange(m.Box.Left, 96, 102);
        Assert.InRange(m.Box.Top, 146, 152);
        Assert.InRange(m.Box.Height, 198, 208);
    }

    [Fact]
    public void A_flat_image_is_a_noop_trim_covering_the_frame()
    {
        var m = Measure(Synthetic.Flat(300, 400, White));
        Assert.True(m.TrimIsNoop);
        Assert.Equal(new Box(0, 0, 300, 400), m.Box);
    }

    [Fact]
    public void Box_edge_count_counts_frame_edges_touched()
    {
        Assert.Equal(0, new Box(10, 10, 50, 50).TouchesEdges(100, 100));
        Assert.Equal(2, new Box(0, 0, 50, 50).TouchesEdges(100, 100));
        Assert.Equal(4, new Box(0, 0, 100, 100).TouchesEdges(100, 100));
    }

    [Fact]
    public void Real_fixtures_measure_without_throwing_and_never_exceed_the_frame()
    {
        foreach (var (name, bytes) in Fixtures.All())
        {
            var info = SourceInspector.Inspect(bytes);
            var m = Measurer.Measure(bytes, info, Recipe.Default);
            Assert.True(m.Box.Left >= 0 && m.Box.Top >= 0, name);
            Assert.True(m.Box.Right <= info.Width && m.Box.Bottom <= info.Height, name);
            Assert.True(m.Box.Width > 0 && m.Box.Height > 0, name);
        }
    }
}
