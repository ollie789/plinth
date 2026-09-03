using Plinth.Core;
using Plinth.Pipeline;
using Plinth.Pipeline.Stores;
using Plinth.Tests.Fakes;

namespace Plinth.Tests;

public class PlinthPipelineTests
{
    private static readonly Rgb White = Rgb.Parse("#ffffff");
    private static readonly Rgb Black = Rgb.Parse("#000000");
    private static byte[] Shot() => Synthetic.PackShot(800, 1000, White, 200, 300, 300, 400, Black);
    private const string Url = "https://cdn.example.com/a.jpg";

    [Fact]
    public async Task First_call_fetches_and_stores_second_call_is_served_from_the_store()
    {
        var fetcher = new FakeFetcher().With(Url, Shot());
        var store = new MemoryStore();
        var p = new PlinthPipeline(fetcher, store, RecipeCatalog.DefaultOnly);

        var first = await p.ProcessUrlAsync(Url, null);
        Assert.Equal("ok", first.Status);
        Assert.False(first.FromStore);
        Assert.Equal(OutputKey.Compute(SourceId.FromUrl(Url), Recipe.Default), first.Record.Key);
        Assert.True(await store.ExistsAsync(first.Record.Key));

        var second = await p.ProcessUrlAsync(Url, null);
        Assert.True(second.FromStore);
        Assert.Equal(first.Bytes, second.Bytes);
        Assert.Equal(1, fetcher.Calls);
    }

    [Fact]
    public async Task A_thumbnail_url_is_upgraded_before_it_is_fetched_keyed_or_recorded()
    {
        const string thumb = "https://m.media-amazon.com/images/I/71abcDEF._AC_SY445_.jpg";
        const string master = "https://m.media-amazon.com/images/I/71abcDEF.jpg";
        // The fake only answers for the master, so a fetch of the thumbnail fails.
        var fetcher = new FakeFetcher().With(master, Shot());
        var p = new PlinthPipeline(fetcher, new MemoryStore(), RecipeCatalog.DefaultOnly);

        var r = await p.ProcessUrlAsync(thumb, null);
        Assert.Equal("ok", r.Status);
        Assert.Equal(master, r.Record.SourceId);
        Assert.Equal(OutputKey.Compute(SourceId.FromUrl(master), Recipe.Default), r.Record.Key);

        // Asking for the master directly is the same request, so it is a store hit.
        var again = await p.ProcessUrlAsync(master, null);
        Assert.True(again.FromStore);
        Assert.Equal(1, fetcher.Calls);

        var inspected = await p.InspectUrlAsync(thumb, null);
        Assert.True(inspected.FromStore);
        Assert.Equal(master, inspected.Record.SourceId);
    }

    [Fact]
    public async Task Fetch_failures_and_bad_urls_become_failed_records_and_are_not_stored()
    {
        var store = new MemoryStore();
        var p = new PlinthPipeline(new FakeFetcher(), store, RecipeCatalog.DefaultOnly);
        var r = await p.ProcessUrlAsync(Url, null);
        Assert.Equal("failed", r.Status);
        Assert.Null(r.Bytes);
        Assert.Contains("404", r.Record.Error);
        Assert.False(await store.ExistsAsync(r.Record.Key));

        var bad = await p.ProcessUrlAsync("http://not-https/a.jpg", null);
        Assert.Equal("failed", bad.Status);
    }

    [Fact]
    public async Task Bytes_path_keys_by_content_when_no_source_id_is_given()
    {
        var p = new PlinthPipeline(new FakeFetcher(), new MemoryStore(), RecipeCatalog.DefaultOnly);
        var bytes = Shot();
        var r = await p.ProcessBytesAsync(bytes, null, null);
        Assert.Equal("ok", r.Status);
        Assert.Equal(OutputKey.Compute(SourceId.FromBytes(bytes), Recipe.Default), r.Record.Key);
    }

    [Fact]
    public async Task Unknown_recipe_is_a_failed_record_not_an_exception()
    {
        var p = new PlinthPipeline(new FakeFetcher().With(Url, Shot()), new MemoryStore(), RecipeCatalog.DefaultOnly);
        var r = await p.ProcessUrlAsync(Url, "nope");
        Assert.Equal("failed", r.Status);
        Assert.Contains("recipe", r.Record.Error);
        Assert.NotEqual(OutputKey.Compute(SourceId.FromUrl(Url), Recipe.Default), r.Record.Key);

        var bytes = Shot();
        var byBytes = await p.ProcessBytesAsync(bytes, "nope", null);
        Assert.Equal("failed", byBytes.Status);
        Assert.NotEqual(OutputKey.Compute(SourceId.FromBytes(bytes), Recipe.Default), byBytes.Record.Key);
    }
}
