using System.Text.Json;
using NetVips;
using Plinth.Core;

namespace Plinth.Tests;

public class NormalizerTests
{
    private static readonly Rgb White = Rgb.Parse("#ffffff");
    private static readonly Rgb Black = Rgb.Parse("#000000");
    private static byte[] Shot() => Synthetic.PackShot(800, 1000, White, 200, 300, 300, 400, Black);

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
        var a = Normalizer.Normalize(bytes, Recipe.Default with { ContentShare = 0.780005 }, "https://a/b.jpg");
        var b = Normalizer.Normalize(bytes, Recipe.Default with { ContentShare = 0.78 }, "https://a/b.jpg");
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
            Assert.True(r.Status is "failed" or "ok", name);
            if (r.Status == "failed") Assert.NotNull(r.Record.Error);
        }
    }
}
