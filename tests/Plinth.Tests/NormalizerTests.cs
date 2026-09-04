using System.Text.Json;
using NetVips;
using Plinth.Core;

namespace Plinth.Tests;

public class NormalizerTests
{
    private static readonly Rgb White = Rgb.Parse("#ffffff");
    private static readonly Rgb Black = Rgb.Parse("#000000");
    private static readonly Rgb Grey = Rgb.Parse("#808080");
    private static byte[] Shot() => Synthetic.PackShot(800, 1000, White, 200, 300, 300, 400, Black);

    /// <summary>
    /// Not a pack shot: content out to the frame edge over a ground that is not
    /// the recipe's. A 2 px border is under a pixel on the 512 px working copy,
    /// so the corner patches land mostly on the content and the sampled ground
    /// comes back near-black — which is what a real scene does too, its corners
    /// being part of the scene rather than a backdrop.
    /// </summary>
    private static byte[] Scene(string format = "jpeg") =>
        Synthetic.PackShot(800, 1000, Grey, 2, 2, 796, 996, Black, format);

    [Fact]
    public void Ok_result_carries_bytes_and_a_complete_record()
    {
        var r = Normalizer.Normalize(Shot(), Recipe.Default, "https://img1.theiconic.com.au/x.jpg");
        Assert.Equal("ok", r.Status);
        Assert.NotNull(r.Output);
        var rec = r.Record;
        Assert.Equal("ok", rec.Status);
        Assert.Null(rec.Error);
        Assert.Equal(Engine.Version, rec.EngineVersion);
        Assert.Equal(Engine.LibvipsVersion, rec.LibvipsVersion);
        // The libvips version is diagnostic only: it is not part of the key.
        Assert.Equal(OutputKey.Compute(rec.SourceId, rec.RecipeHash, rec.EngineVersion), rec.Key);
        Assert.Equal(Recipe.Default.Hash, rec.RecipeHash);
        Assert.Equal(OutputKey.Compute("https://img1.theiconic.com.au/x.jpg", Recipe.Default), rec.Key);
        Assert.Equal("jpeg", rec.Source.Format);
        Assert.Equal(800, rec.Source.Width);
        Assert.Equal(64, rec.Source.Sha256.Length);
        Assert.Equal("#ffffff", rec.Ground.Sampled);
        Assert.InRange(rec.Trim.Width, 298, 312);
        Assert.True(rec.Verdict.PackShot);
        Assert.Equal(1000, rec.Output!.Width);
        Assert.Equal(r.Output!.Length, rec.Output.Bytes);
        Assert.True(rec.TimingsMs.Encode >= 0);
    }

    [Fact]
    public void Without_a_source_id_the_key_derives_from_the_bytes()
    {
        var bytes = Shot();
        var r = Normalizer.Normalize(bytes, Recipe.Default);
        Assert.Equal(OutputKey.Compute(SourceId.FromBytes(bytes), Recipe.Default), r.Record.Key);
    }

    [Fact]
    public void Bad_bytes_fail_with_a_record_and_no_output_and_do_not_throw()
    {
        var r = Normalizer.Normalize("nope"u8.ToArray(), Recipe.Default, "https://a/b.jpg");
        Assert.Equal("failed", r.Status);
        Assert.Null(r.Output);
        Assert.Equal("failed", r.Record.Status);
        Assert.Contains("not a supported image", r.Record.Error);
        Assert.Equal(OutputKey.Compute("https://a/b.jpg", Recipe.Default), r.Record.Key);
    }

    [Fact]
    public void An_already_normalised_image_passes_through_untouched()
    {
        var first = Normalizer.Normalize(Shot(), Recipe.Default);
        var again = Normalizer.Normalize(first.Output!, Recipe.Default);
        Assert.Equal("passthrough", again.Status);
        Assert.Same(first.Output, again.Output);
        Assert.Equal("passthrough", again.Record.Status);
        Assert.Equal("already-normalised", again.Record.PassthroughReason);
    }

    [Fact]
    public void An_editorial_image_passes_through_untouched_unless_the_recipe_says_card()
    {
        var scene = Scene();
        var r = Normalizer.Normalize(scene, Recipe.Default);
        Assert.False(r.Record.Verdict.PackShot);
        Assert.Equal(["ground-not-background", "touches-edges", "content-fills-frame"], r.Record.Verdict.Reasons);
        Assert.False(r.Record.Ground.MatchesBackground);
        Assert.Equal("#222222", r.Record.Ground.Sampled);
        Assert.Equal("passthrough", r.Status);
        Assert.Equal("passthrough", r.Record.Status);
        Assert.Equal("editorial", r.Record.PassthroughReason);
        Assert.Equal("jpeg", r.Record.Output!.Format);
        Assert.Equal(scene.Length, r.Record.Output.Bytes);
        Assert.Equal(scene, r.Output);

        var carded = Normalizer.Normalize(scene, Recipe.Default with { Editorial = "card" });
        Assert.Equal("ok", carded.Status);
        Assert.Null(carded.Record.PassthroughReason);
    }

    [Fact]
    public void A_pack_shot_that_already_fills_its_frame_passes_through_untouched()
    {
        // A 780x960 box in an 800x1000 frame is 0.975 of it before any trim:
        // the canvas has nothing to add, and carding would only shrink the
        // product behind a margin.
        var framed = Synthetic.PackShot(800, 1000, White, 10, 20, 780, 960, Black);
        var r = Normalizer.Normalize(framed, Recipe.Default);
        Assert.True(r.Record.Verdict.PackShot);
        Assert.InRange(r.Record.Trim.ContentShareBefore, Normalizer.FramedFill, 1.0);
        Assert.Equal("passthrough", r.Status);
        Assert.Equal("framed", r.Record.PassthroughReason);
        Assert.Equal("jpeg", r.Record.Output!.Format);
        Assert.Equal(framed, r.Output);

        var carded = Normalizer.Normalize(framed, Recipe.Default with { Editorial = "card" });
        Assert.Equal("ok", carded.Status);
        Assert.Null(carded.Record.PassthroughReason);
        Assert.Equal((1000, 1250), (carded.Record.Output!.Width, carded.Record.Output.Height));
    }

    [Fact]
    public void A_wide_product_lying_across_its_frame_is_carded_not_passed_through()
    {
        // The frypan case. A 950x206 box in a 1000x1000 frame fills 95% of the
        // width and 21% of the height. Measured on the wider axis alone it
        // looks framed; it is a strip of product in a square of air, and
        // handing it back leaves the consumer's tile to crop the handle off.
        var strip = Synthetic.PackShot(1000, 1000, White, 25, 400, 950, 206, Black);
        var r = Normalizer.Normalize(strip, Recipe.Default);
        Assert.True(r.Record.Verdict.PackShot);
        Assert.InRange(r.Record.Trim.ContentShareBefore, Normalizer.FramedFill, 1.0);
        Assert.Equal("ok", r.Status);
        Assert.Null(r.Record.PassthroughReason);
        Assert.Equal((1000, 1250), (r.Record.Output!.Width, r.Record.Output.Height));
    }

    [Fact]
    public void A_frame_wider_than_the_canvas_is_carded_however_full_it_is()
    {
        // The shoe case. A 2208x989 box in a 2400x1075 frame fills 92% of both
        // axes, so the frame is the product's own — but it is 2.2 times wider
        // than it is tall,
        // and a 4:5 tile fitting that shows the middle third of the shoe.
        // Carding puts the whole shoe on the canvas the tile expects.
        var wide = Synthetic.PackShot(2400, 1075, White, 96, 43, 2208, 989, Black);
        var r = Normalizer.Normalize(wide, Recipe.Default);
        Assert.True(r.Record.Verdict.PackShot);
        Assert.Equal("ok", r.Status);
        Assert.Null(r.Record.PassthroughReason);
        Assert.Equal((1000, 1250), (r.Record.Output!.Width, r.Record.Output.Height));
    }

    [Fact]
    public void A_frame_taller_than_the_canvas_still_passes_through()
    {
        // The other side of the aspect bound, and the reason it is one-sided:
        // a 2:3 portrait model shot loses a little off the top and bottom to
        // the tile and stays legible. Carding it would shrink it behind a
        // margin, which is the complaint the framed rule exists to answer.
        var portrait = Synthetic.PackShot(800, 1200, White, 32, 48, 736, 1104, Black);
        var r = Normalizer.Normalize(portrait, Recipe.Default);
        Assert.True(r.Record.Verdict.PackShot);
        Assert.Equal("passthrough", r.Status);
        Assert.Equal("framed", r.Record.PassthroughReason);
        Assert.Equal(portrait, r.Output);
    }

    [Fact]
    public void A_padded_pack_shot_is_still_carded()
    {
        // The same frame with a 300x400 box is 0.4 of it: air to remove, so the
        // canvas earns its place.
        var r = Normalizer.Normalize(Shot(), Recipe.Default);
        Assert.True(r.Record.Trim.ContentShareBefore < Normalizer.FramedFill);
        Assert.Equal("ok", r.Status);
        Assert.Null(r.Record.PassthroughReason);
    }

    [Fact]
    public void An_editorial_image_a_browser_cannot_show_is_carded_rather_than_passed_through()
    {
        // Passthrough hands back the source's own bytes and format, so it is
        // only ever an option for a format the page could render.
        var r = Normalizer.Normalize(Scene("tiff"), Recipe.Default);
        Assert.Equal("tiff", r.Record.Source.Format);
        Assert.False(r.Record.Verdict.PackShot);
        Assert.Equal("ok", r.Status);
        Assert.Null(r.Record.PassthroughReason);
        Assert.Equal("webp", r.Record.Output!.Format);
    }

    [Fact]
    public void A_tinted_ground_is_balanced_onto_white_so_no_box_survives_the_trim()
    {
        // #ededed is 18 from white: close enough to card on white, and today
        // that leaves the trimmed box carrying its tint into the tile. Two
        // squares at opposite corners make a bounding box that still contains
        // ground, which is the only way the tint reaches the card at all.
        var recipe = Recipe.Default with { Format = "png" };
        var r = Normalizer.Normalize(
            Synthetic.DiagonalPackShot(800, 1000, Rgb.Parse("#ededed"), 200, 300, 300, 400, 100, Rgb.Parse("#4060a0")),
            recipe);

        Assert.Equal("ok", r.Status);
        Assert.True(r.Record.Verdict.PackShot);
        Assert.Equal("#ededed", r.Record.Ground.Sampled);
        Assert.True(r.Record.Ground.MatchesBackground);
        Assert.True(r.Record.Ground.Balanced);

        using var img = Image.NewFromBuffer(r.Output!);
        // The canvas the content was extended onto...
        AssertPixel(img, 2, 2, [255, 255, 255], 1);
        AssertPixel(img, 40, 40, [255, 255, 255], 1);
        // ...and the ground that came through the trim inside the content, which
        // is the rectangle this rule exists to remove. The gap between the two
        // squares lands on the middle of the tile.
        AssertPixel(img, 500, 625, [255, 255, 255], 1);

        // The product brightens by the same factor the backdrop did.
        var scale = 255 / 237.0;
        AssertPixel(img, 234, 227, [Math.Round(0x40 * scale), Math.Round(0x60 * scale), Math.Round(0xa0 * scale)], 2);
    }

    [Fact]
    public void A_ground_that_already_is_the_background_is_left_alone()
    {
        var r = Normalizer.Normalize(Shot(), Recipe.Default);
        Assert.Equal("#ffffff", r.Record.Ground.Sampled);
        Assert.False(r.Record.Ground.Balanced);
    }

    [Fact]
    public void Balancing_aims_at_whatever_background_the_recipe_asks_for()
    {
        var recipe = Recipe.Default with { Format = "png", Background = Rgb.Parse("#fafafa") };
        var r = Normalizer.Normalize(
            Synthetic.DiagonalPackShot(800, 1000, Rgb.Parse("#ededed"), 200, 300, 300, 400, 100, Rgb.Parse("#4060a0")),
            recipe);

        Assert.Equal("ok", r.Status);
        Assert.True(r.Record.Ground.Balanced);
        using var img = Image.NewFromBuffer(r.Output!);
        AssertPixel(img, 2, 2, [0xfa, 0xfa, 0xfa], 1);
        // The ground inside the trim lands on the recipe background, not white.
        AssertPixel(img, 500, 625, [0xfa, 0xfa, 0xfa], 1);
    }

    [Fact]
    public void Balancing_can_darken_a_ground_onto_a_dimmer_background()
    {
        // The other direction: a white ground against an off-white background
        // scales by 250/255, under 1, and comes down onto the background.
        var recipe = Recipe.Default with { Format = "png", Background = Rgb.Parse("#fafafa") };
        var r = Normalizer.Normalize(
            Synthetic.DiagonalPackShot(800, 1000, White, 200, 300, 300, 400, 100, Rgb.Parse("#4060a0"), "png"),
            recipe);

        Assert.Equal("ok", r.Status);
        Assert.Equal("#ffffff", r.Record.Ground.Sampled);
        Assert.True(r.Record.Ground.Balanced);
        using var img = Image.NewFromBuffer(r.Output!);
        AssertPixel(img, 2, 2, [0xfa, 0xfa, 0xfa], 1);
        // The ground that came through the trim is darker than it was.
        AssertPixel(img, 500, 625, [0xfa, 0xfa, 0xfa], 1);
    }

    [Fact]
    public void A_scale_no_tint_could_justify_is_refused_outright()
    {
        // #013232 is 40 from #293232 — inside the distance band — but the red
        // channel would be multiplied by 41. Chebyshev distance bounds how far
        // a channel moves, not the ratio it moves by, so the caps have to.
        var recipe = Recipe.Default with { Format = "png", Background = Rgb.Parse("#293232") };
        var r = Normalizer.Normalize(
            Synthetic.DiagonalPackShot(800, 1000, Rgb.Parse("#013232"), 200, 300, 300, 400, 100, Rgb.Parse("#4060a0"), "png"),
            recipe);

        Assert.Equal("ok", r.Status);
        Assert.Equal("#013232", r.Record.Ground.Sampled);
        Assert.True(r.Record.Ground.MatchesBackground);
        Assert.True(r.Record.Verdict.PackShot);
        Assert.False(r.Record.Ground.Balanced);
        // Left alone, it cards on the recipe background exactly as it did before.
        using var img = Image.NewFromBuffer(r.Output!);
        AssertPixel(img, 2, 2, [0x29, 0x32, 0x32], 1);
    }

    [Theory]
    [InlineData("#fdfdfd", 2, false)]
    [InlineData("#fcfcfc", 3, true)]
    public void The_minimum_distance_is_a_closed_bound(string ground, int distance, bool balanced)
    {
        // At the bound the ground already is the background; one level past it,
        // balancing starts. A lossless source keeps the sampled ground exact,
        // so the boundary is the boundary and not a rounding artefact.
        var r = Normalizer.Normalize(
            Synthetic.DiagonalPackShot(800, 1000, Rgb.Parse(ground), 200, 300, 300, 400, 100, Black, "png"),
            Recipe.Default with { Format = "png" });

        Assert.Equal(ground, r.Record.Ground.Sampled);
        Assert.Equal(distance, Rgb.Parse(ground).Distance(White));
        Assert.Equal(2, Normalizer.GroundBalanceMinDistance);
        Assert.Equal(balanced, r.Record.Ground.Balanced);
    }

    private static void AssertPixel(Image img, int x, int y, double[] expected, int tolerance)
    {
        var got = img.Getpoint(x, y);
        for (var band = 0; band < expected.Length; band++)
            Assert.InRange(got[band], expected[band] - tolerance, expected[band] + tolerance);
    }

    [Fact]
    public void A_pack_shot_on_a_grey_ground_is_carded_on_that_grey_not_on_white()
    {
        var r = Normalizer.Normalize(Synthetic.PackShot(800, 1000, Grey, 250, 300, 300, 400, Black), Recipe.Default);
        Assert.True(r.Record.Verdict.PackShot);
        Assert.Equal("ok", r.Status);
        Assert.False(r.Record.Ground.MatchesBackground);
        // Too far from the background to be balanced towards it: it cards on
        // its own ground instead, which is the existing rule.
        Assert.False(r.Record.Ground.Balanced);

        using var img = Image.NewFromBuffer(r.Output!);
        var corner = img.Getpoint(2, 2);
        for (var band = 0; band < 3; band++)
            Assert.InRange(corner[band], 0x80 - 3, 0x80 + 3);
    }

    [Fact]
    public void A_source_that_carries_metadata_does_not_pass_through()
    {
        var clean = Normalizer.Normalize(Shot(), Recipe.Default).Output!;
        Assert.Equal("passthrough", Normalizer.Normalize(clean, Recipe.Default).Status);

        foreach (var tagged in new[] { WithIccProfile(clean), WithExif(clean) })
        {
            using var check = Image.NewFromBuffer(tagged);
            Assert.True(check.GetTypeOf("icc-profile-data") != IntPtr.Zero || check.GetTypeOf("exif-data") != IntPtr.Zero);
            Assert.True(SourceInspector.Inspect(tagged).HasMetadata);
            var r = Normalizer.Normalize(tagged, Recipe.Default);
            Assert.Equal("ok", r.Status);
        }
    }

    [Fact]
    public void A_smaller_source_with_the_right_aspect_is_rendered_not_passed_through()
    {
        // 400x500 is the canvas aspect exactly, on the recipe ground, with the
        // content share the recipe wants - but it is not the canvas size.
        var small = Synthetic.PackShot(400, 500, White, 44, 55, 312, 390, Black, format: "webp");
        var r = Normalizer.Normalize(small, Recipe.Default);
        Assert.Equal("ok", r.Status);
        Assert.Equal((1000, 1250), (r.Record.Output!.Width, r.Record.Output.Height));
    }

    private static byte[] WithIccProfile(byte[] webp)
    {
        using var src = Image.NewFromBuffer(webp);
        using var probe = Image.Black(1, 1, bands: 3).Copy(interpretation: Enums.Interpretation.Srgb)
            .IccExport(outputProfile: "srgb");
        var profile = (byte[])probe.Get("icc-profile-data");
        using var tagged = src.Mutate(m => m.Set(GValue.BlobType, "icc-profile-data", profile));
        return tagged.WebpsaveBuffer(q: Recipe.Default.Quality, effort: 4, smartSubsample: false,
            keep: Enums.ForeignKeep.All);
    }

    private static byte[] WithExif(byte[] webp)
    {
        using var src = Image.NewFromBuffer(webp);
        using var tagged = src.Mutate(m => m.Set(GValue.RefStrType, "exif-ifd0-Orientation",
            "1 (Top-left, Short, 1 components, 2 bytes)"));
        return tagged.WebpsaveBuffer(q: Recipe.Default.Quality, effort: 4, smartSubsample: false,
            keep: Enums.ForeignKeep.All);
    }

    [Fact]
    public void Record_round_trips_through_camel_case_json()
    {
        var r = Normalizer.Normalize(Shot(), Recipe.Default, "https://a/b.jpg");
        var json = r.Record.ToJson();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.True(doc.RootElement.GetProperty("verdict").GetProperty("packShot").GetBoolean());
        Assert.Equal(r.Record.Key, ResultRecord.FromJson(json).Key);
        Assert.DoesNotContain("\n", json);
    }

    [Fact]
    public void An_unsupported_output_format_fails_instead_of_silently_encoding_webp()
    {
        var r = Normalizer.Normalize(Shot(), Recipe.Default with { Format = "jpeg" }, "https://a/b.jpg");
        Assert.Equal("failed", r.Status);
        Assert.Null(r.Output);
        Assert.Contains("format", r.Record.Error);
    }

    [Fact]
    public void An_out_of_range_recipe_field_fails_instead_of_throwing()
    {
        var r = Normalizer.Normalize(Shot(), Recipe.Default with { Quality = 999 }, "https://a/b.jpg");
        Assert.Equal("failed", r.Status);
        Assert.NotNull(r.Record.Error);
    }

    [Fact]
    public void A_contentShare_beyond_four_decimals_is_rounded_at_the_boundary()
    {
        var bytes = Shot();
        var a = Normalizer.Normalize(bytes, Recipe.Default with { ContentShare = 0.850005 }, "https://a/b.jpg");
        var b = Normalizer.Normalize(bytes, Recipe.Default with { ContentShare = 0.85 }, "https://a/b.jpg");
        Assert.Equal("ok", a.Status);
        Assert.Equal(b.Record.RecipeHash, a.Record.RecipeHash);
        Assert.Equal(b.Record.Key, a.Record.Key);
        Assert.Equal(b.Output!.Length, a.Output!.Length);
    }

    [Fact]
    public void A_truncated_fixture_fails_with_a_record_instead_of_throwing()
    {
        // libvips happily decodes a baseline JPEG that stops early, filling the
        // missing scan with grey, so a fixture whose header itself is cut short is
        // what actually reaches the failure path.
        var bytes = Fixtures.Read("img1-theiconic-com-au.jpg");
        var r = Normalizer.Normalize(bytes[..(bytes.Length * 5 / 100)], Recipe.Default, "https://x/t.jpg");

        Assert.Equal("failed", r.Status);
        Assert.Null(r.Output);
        Assert.NotNull(r.Record.Error);
        Assert.NotNull(r.Record.Key);
    }

    [Fact]
    public void No_truncated_fixture_throws_out_of_normalize()
    {
        foreach (var (name, bytes) in Fixtures.All())
        {
            var r = Normalizer.Normalize(bytes[..(bytes.Length * 5 / 100)], Recipe.Default, "https://x/" + name);
            Assert.True(r.Status is "failed" or "ok" or "passthrough", name);
            if (r.Status == "failed") Assert.NotNull(r.Record.Error);
        }
    }

    [Fact]
    public void A_cancelled_token_surfaces_as_cancellation_not_a_failed_record()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            Normalizer.Normalize(Shot(), Recipe.Default, "https://a/b.jpg", cts.Token));
    }
}
