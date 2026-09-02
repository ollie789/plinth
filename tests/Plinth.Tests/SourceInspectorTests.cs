using Plinth.Core;

namespace Plinth.Tests;

public class SourceInspectorTests
{
    private static readonly Rgb White = Rgb.Parse("#ffffff");
    private static readonly Rgb Black = Rgb.Parse("#000000");

    [Fact]
    public void Reports_format_size_and_alpha_for_jpeg()
    {
        var info = SourceInspector.Inspect(Synthetic.PackShot(800, 1000, White, 200, 300, 300, 400, Black));
        Assert.Equal("jpeg", info.Format);
        Assert.Equal(800, info.Width);
        Assert.Equal(1000, info.Height);
        Assert.False(info.HasAlpha);
        Assert.Equal(1, info.Pages);
    }

    [Fact]
    public void Reports_alpha_for_transparent_png()
    {
        var info = SourceInspector.Inspect(Synthetic.TransparentPackShot(400, 500, 100, 100, 100, 200, Black));
        Assert.Equal("png", info.Format);
        Assert.True(info.HasAlpha);
    }

    [Fact]
    public void Rejects_images_over_the_pixel_cap_before_decoding()
    {
        var bytes = Synthetic.PackShot(200, 200, White, 50, 50, 50, 50, Black);
        var ex = Assert.Throws<PlinthException>(() => SourceInspector.Inspect(bytes, maxPixels: 10_000));
        Assert.Contains("too large", ex.Message);
    }

    [Fact]
    public void Rejects_bytes_that_are_not_an_image()
    {
        var ex = Assert.Throws<PlinthException>(() => SourceInspector.Inspect("not an image"u8.ToArray()));
        Assert.Contains("not a supported image", ex.Message);
    }

    [Fact]
    public void Real_fixtures_all_inspect()
    {
        foreach (var (name, bytes) in Fixtures.All())
        {
            var info = SourceInspector.Inspect(bytes);
            Assert.True(info.Width > 100 && info.Height > 100, name);
        }
    }
}
