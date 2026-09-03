using System.Security.Cryptography;
using System.Text;
using Plinth.Core;

namespace Plinth.Tests;

public class RecipeTests
{
    [Fact]
    public void Default_matches_the_spec()
    {
        var r = Recipe.Default;
        Assert.Equal("4:5", r.Aspect);
        Assert.Equal(1000, r.Width);
        Assert.Equal(0.85, r.ContentShare);
        Assert.Equal("#ffffff", r.Background.ToHex());
        Assert.Equal(12, r.TrimThreshold);
        Assert.Equal("webp", r.Format);
        Assert.Equal(84, r.Quality);
        Assert.True(r.Upscale);
        Assert.Equal("passthrough", r.Editorial);
    }

    [Fact]
    public void Canonical_json_has_sorted_keys_and_no_whitespace()
    {
        Assert.Equal(
            "{\"aspect\":\"4:5\",\"background\":\"#ffffff\",\"contentShare\":0.85,\"editorial\":\"passthrough\",\"format\":\"webp\",\"quality\":84,\"trimThreshold\":12,\"upscale\":true,\"width\":1000}",
            Recipe.Default.Canonical());
    }

    [Fact]
    public void Hash_is_first_16_hex_of_sha256_of_canonical()
    {
        var expected = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(Recipe.Default.Canonical())))[..16];
        Assert.Equal(expected, Recipe.Default.Hash);
    }

    [Fact]
    public void Any_field_change_changes_the_hash()
    {
        var a = Recipe.Default;
        Assert.NotEqual(a.Hash, (a with { Quality = 80 }).Hash);
        Assert.NotEqual(a.Hash, (a with { Background = Rgb.Parse("#fafafa") }).Hash);
        Assert.NotEqual(a.Hash, (a with { Editorial = "card" }).Hash);
        Assert.Equal(a.Hash, (a with { Quality = 84 }).Hash);
    }

    [Fact]
    public void Canvas_and_content_box_derive_from_aspect_width_and_share()
    {
        var r = Recipe.Default;
        Assert.Equal(1000, r.CanvasWidth);
        Assert.Equal(1250, r.CanvasHeight);
        Assert.Equal(850, r.ContentBoxWidth);
        Assert.Equal(1062, r.ContentBoxHeight);
        var sq = r with { Aspect = "1:1", Width = 800 };
        Assert.Equal(800, sq.CanvasHeight);
    }

    [Fact]
    public void FromJson_accepts_partial_objects_and_rejects_bad_values()
    {
        var r = Recipe.FromJson("{\"quality\":70,\"format\":\"png\",\"editorial\":\"card\"}");
        Assert.Equal(70, r.Quality);
        Assert.Equal("png", r.Format);
        Assert.Equal(1000, r.Width);
        Assert.Equal("card", r.Editorial);
        Assert.Throws<PlinthException>(() => Recipe.FromJson("{\"aspect\":\"wide\"}"));
        Assert.Throws<PlinthException>(() => Recipe.FromJson("{\"format\":\"gif\"}"));
        Assert.Throws<PlinthException>(() => Recipe.FromJson("{\"contentShare\":1.5}"));
        Assert.Throws<PlinthException>(() => Recipe.FromJson("{\"editorial\":\"shrink\"}"));
    }

    [Fact]
    public void FromJson_wraps_malformed_or_wrong_shaped_input_in_PlinthException()
    {
        Assert.Throws<PlinthException>(() => Recipe.FromJson("\"hello\""));
        Assert.Throws<PlinthException>(() => Recipe.FromJson("[1,2,3]"));
        Assert.Throws<PlinthException>(() => Recipe.FromJson("{\"width\":\"foo\"}"));
        Assert.Throws<PlinthException>(() => Recipe.FromJson("{\"width\":1.5}"));
        Assert.Throws<PlinthException>(() => Recipe.FromJson("{not json"));
    }

    [Fact]
    public void Validated_rounds_contentShare_so_the_hash_and_the_box_agree()
    {
        var a = (Recipe.Default with { ContentShare = 0.850005 }).Validated();
        Assert.Equal(0.85, a.ContentShare);
        Assert.Equal(Recipe.Default.Hash, a.Hash);
        Assert.Equal(Recipe.Default.ContentBoxWidth, a.ContentBoxWidth);
    }

    [Fact]
    public void Rgb_parses_hex_and_measures_chebyshev_distance()
    {
        var a = Rgb.Parse("#ffffff");
        var b = Rgb.Parse("#f0ff00");
        Assert.Equal(255, a.Distance(b));
        Assert.Equal(new double[] { 255, 255, 255 }, a.ToVips());
        Assert.Throws<PlinthException>(() => Rgb.Parse("white"));
    }
}
