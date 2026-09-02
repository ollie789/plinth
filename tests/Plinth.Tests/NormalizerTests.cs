using System.Text.Json;
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
}
