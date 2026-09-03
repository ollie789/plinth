using NetVips;
using Plinth.Core;

namespace Plinth.Tests;

public class RendererTests
{
    private static readonly Rgb White = Rgb.Parse("#ffffff");
    private static readonly Rgb Black = Rgb.Parse("#000000");

    private static Rendered RenderSynthetic(Recipe recipe, int w = 800, int h = 1000)
    {
        var bytes = Synthetic.PackShot(w, h, White, 200, 300, 300, 400, Black);
        var info = SourceInspector.Inspect(bytes);
        var m = Measurer.Measure(bytes, info, recipe);
        return Renderer.Render(bytes, info, m, recipe, recipe.Background);
    }

    private static Measurement Box(int w, int h) =>
        new(new GroundInfo(White, 0, true), new Box(0, 0, w, h), false, 0.5, 512, 512);

    [Fact]
    public void Output_is_the_recipe_canvas_with_content_fitted_and_centred()
    {
        var r = RenderSynthetic(Recipe.Default);
        Assert.Equal(1000, r.Info.Width);
        Assert.Equal(1250, r.Info.Height);
        Assert.Equal("webp", r.Info.Format);
        using var img = Image.NewFromBuffer(r.Bytes);
        var raw = img.FindTrim(threshold: 12, background: [255, 255, 255]);
        var t = raw.Select(Convert.ToInt32).ToArray();
        // 300x400 box scaled to fit 850x1062: limited by height -> 797x1062
        Assert.InRange(t[3], 1048, 1058);
        Assert.InRange(t[2], 784, 800);
        Assert.InRange(t[0], 97, 110);
        Assert.InRange(t[1], 93, 105);
    }

    [Fact]
    public void Wide_content_is_given_more_of_the_tile_width_than_the_recipe_share()
    {
        // A trimmed shoe box runs 2.0-2.3 wide to tall, so at 85% of the tile
        // width it covers about a third of the tile. Past 1.6 the box widens.
        Assert.Equal(920, Renderer.ContentBoxWidthFor(Box(600, 200), Recipe.Default));
        Assert.Equal(920, Renderer.ContentBoxWidthFor(Box(320, 200), Recipe.Default));
        // Just under the ratio, and portrait content, keep the recipe share.
        Assert.Equal(850, Renderer.ContentBoxWidthFor(Box(318, 200), Recipe.Default));
        Assert.Equal(850, Renderer.ContentBoxWidthFor(Box(300, 400), Recipe.Default));
        // The height box never moves, so a wide item gains width, not height.
        Assert.Equal(1062, Recipe.Default.ContentBoxHeight);
    }

    [Fact]
    public void A_wide_box_renders_across_the_wider_content_box_and_stays_centred()
    {
        var bytes = Synthetic.PackShot(800, 1000, White, 100, 400, 600, 200, Black);
        var info = SourceInspector.Inspect(bytes);
        var m = Measurer.Measure(bytes, info, Recipe.Default);
        var r = Renderer.Render(bytes, info, m, Recipe.Default, White);

        using var img = Image.NewFromBuffer(r.Bytes);
        var t = img.FindTrim(threshold: 12, background: [255, 255, 255]).Select(Convert.ToInt32).ToArray();
        // 600x200 is 3.0 wide, so it fits the 920 box rather than the 850 one.
        Assert.InRange(t[2], 918, 922);
        Assert.InRange(t[0] + t[2] / 2.0, 495, 505);
        Assert.InRange(t[1] + t[3] / 2.0, 620, 630);
    }

    [Fact]
    public void A_wide_box_decodes_enough_pixels_to_cover_the_wider_content_box()
    {
        var info = new SourceInfo("jpeg", 4000, 5000, false, 1, 1);
        var m = Box(2400, 800) with { Box = new Box(800, 2100, 2400, 800) };
        var (w, _) = Renderer.DecodeSizeFor(info, m, Recipe.Default);
        // Whatever the decode target, the box within it must still be able to
        // reach the 920 px it is about to be fitted into.
        Assert.True(w * (m.Box.Width / 4000.0) >= Renderer.ContentBoxWidthFor(m, Recipe.Default),
            $"decode {w} leaves the box short of {Renderer.ContentBoxWidthFor(m, Recipe.Default)}");
    }

    [Fact]
    public void The_renderer_applies_the_ground_scale_it_is_handed_and_decides_nothing()
    {
        var recipe = Recipe.Default with { Format = "png" };
        var bytes = Synthetic.DiagonalPackShot(800, 1000, Rgb.Parse("#ededed"), 200, 300, 300, 400, 100, Black);
        var info = SourceInspector.Inspect(bytes);
        var m = Measurer.Measure(bytes, info, recipe);

        // Handed no scale, the tint comes through the trim exactly as measured.
        var plain = Renderer.Render(bytes, info, m, recipe, White);
        using (var img = Image.NewFromBuffer(plain.Bytes))
            Assert.InRange(img.Getpoint(500, 625)[0], 0xed - 2, 0xed + 2);

        // Handed one, the same ground lands on the canvas colour.
        var balanced = Renderer.Render(bytes, info, m, recipe, White, [255 / 237.0, 255 / 237.0, 255 / 237.0]);
        using (var img = Image.NewFromBuffer(balanced.Bytes))
            Assert.InRange(img.Getpoint(500, 625)[0], 254, 255);
    }

    [Fact]
    public void Png_recipe_encodes_png()
    {
        var r = RenderSynthetic(Recipe.Default with { Format = "png" });
        Assert.Equal("png", r.Info.Format);
        Assert.Equal("VipsForeignLoadPngBuffer", Image.FindLoadBuffer(r.Bytes));
    }

    [Fact]
    public void Decode_size_never_exceeds_the_source_and_covers_the_content_box()
    {
        var info = new SourceInfo("jpeg", 4000, 5000, false, 1, 1);
        var m = new Measurement(new GroundInfo(White, 0, true), new Box(1000, 1000, 2000, 2500), false, 0.5, 410, 512);
        var (w, h) = Renderer.DecodeSizeFor(info, m, Recipe.Default);
        // box must reach 850x1062: scale 0.425 -> 1700x2125 (+1 slack)
        Assert.InRange(w, 1700, 1702);
        Assert.InRange(h, 2125, 2127);
        var small = new SourceInfo("jpeg", 400, 500, false, 1, 1);
        var ms = new Measurement(new GroundInfo(White, 0, true), new Box(100, 100, 200, 250), false, 0.5, 400, 500);
        Assert.Equal((401, 501), Renderer.DecodeSizeFor(small, ms, Recipe.Default));
    }

    [Fact]
    public void No_upscale_leaves_small_content_small()
    {
        // Source deliberately smaller than the default 800x1000: still large enough
        // to hold the 300x400 box at (200,300) uncropped (needs >= 500x700).
        var r = RenderSynthetic(Recipe.Default with { Upscale = false }, 600, 800);
        using var img = Image.NewFromBuffer(r.Bytes);
        var raw = img.FindTrim(threshold: 12, background: [255, 255, 255]);
        var t = raw.Select(Convert.ToInt32).ToArray();
        Assert.InRange(t[2], 296, 306);
        Assert.InRange(t[3], 396, 406);
    }

    [Fact]
    public void Renders_through_the_full_chain_at_the_minimum_canvas_width()
    {
        // Exercises MakeOpaqueSrgb, crop, fit and gravity all the way through
        // at the recipe's smallest allowed canvas (16px wide).
        var r = RenderSynthetic(Recipe.Default with { Format = "png", Width = 16 });
        Assert.Equal(16, r.Info.Width);
        Assert.Equal("png", r.Info.Format);
        Assert.True(r.Bytes.Length > 0);
    }

    [Fact]
    public void An_undecodable_source_throws_PlinthException_with_a_decode_message()
    {
        var bytes = "not an image"u8.ToArray();
        var info = new SourceInfo("jpeg", 100, 100, false, 1, 1);
        var m = new Measurement(new GroundInfo(White, 0, true), new Box(0, 0, 100, 100), true, 1.0, 100, 100);
        var ex = Assert.Throws<PlinthException>(() => Renderer.Render(bytes, info, m, Recipe.Default, White));
        Assert.Contains("could not decode source", ex.Message);
    }

    [Fact]
    public void Real_fixtures_render_to_the_canvas()
    {
        foreach (var (name, bytes) in Fixtures.All())
        {
            var info = SourceInspector.Inspect(bytes);
            var m = Measurer.Measure(bytes, info, Recipe.Default);
            var r = Renderer.Render(bytes, info, m, Recipe.Default, White);
            Assert.Equal((1000, 1250), (r.Info.Width, r.Info.Height));
            Assert.True(r.Bytes.Length < 200_000, $"{name} is {r.Bytes.Length} bytes");
        }
    }
}
