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
