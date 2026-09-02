using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Plinth.Api;
using Plinth.Core;
using Plinth.Pipeline;
using Plinth.Pipeline.Fetch;
using Plinth.Pipeline.Stores;
using Plinth.Tests.Fakes;

namespace Plinth.Tests.Api;

public class ApiTests : IAsyncLifetime
{
    private const string Url = "https://cdn.example.com/a.jpg";
    private static readonly Rgb White = Rgb.Parse("#ffffff");
    private static readonly Rgb Black = Rgb.Parse("#000000");
    private static byte[] Shot() => Synthetic.PackShot(800, 1000, White, 200, 300, 300, 400, Black);

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private readonly FakeFetcher _fetcher = new FakeFetcher().With(Url, Shot());
    private readonly MemoryStore _store = new();

    private PipelineOptions Options(string? signingKey = null, string onFailure = "redirect") =>
        new(FetchPolicy.FromHostList("cdn.example.com"), "none", RecipeCatalog.DefaultOnly, signingKey, onFailure, 1);

    private async Task StartAsync(PipelineOptions options)
    {
        _app = ApiHost.Build([], options, b =>
        {
            b.WebHost.UseTestServer();
            b.Services.AddSingleton<ISourceFetcher>(_fetcher);
            b.Services.AddSingleton<IOutputStore>(_store);
        });
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public Task InitializeAsync() => StartAsync(Options());
    public async Task DisposeAsync() { await _app.StopAsync(); await _app.DisposeAsync(); }

    [Fact]
    public async Task Image_route_returns_the_normalised_tile_with_headers_and_caches_the_second_call()
    {
        var r = await _client.GetAsync($"/v1/image?src={Uri.EscapeDataString(Url)}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("image/webp", r.Content.Headers.ContentType!.MediaType);
        Assert.Equal("public, max-age=31536000, immutable", r.Headers.CacheControl!.ToString());
        Assert.Equal("ok", r.Headers.GetValues("X-Plinth-Status").Single());
        Assert.Equal("miss", r.Headers.GetValues("X-Plinth-Cache").Single());
        Assert.Equal("true", r.Headers.GetValues("X-Plinth-Verdict").Single());
        Assert.Equal(64, r.Headers.GetValues("X-Plinth-Key").Single().Length);
        Assert.True((await r.Content.ReadAsByteArrayAsync()).Length > 1000);

        var again = await _client.GetAsync($"/v1/image?src={Uri.EscapeDataString(Url)}");
        Assert.Equal("hit", again.Headers.GetValues("X-Plinth-Cache").Single());
        Assert.Equal(1, _fetcher.Calls);
    }

    [Fact]
    public async Task Failure_redirects_to_the_source_by_default_and_errors_when_configured()
    {
        var missing = "https://cdn.example.com/missing.jpg";
        var r = await _client.GetAsync($"/v1/image?src={Uri.EscapeDataString(missing)}");
        Assert.Equal(HttpStatusCode.Found, r.StatusCode);
        Assert.Equal(missing, r.Headers.Location!.ToString());
        Assert.Equal("failed", r.Headers.GetValues("X-Plinth-Status").Single());

        await DisposeAsync();
        await StartAsync(Options(onFailure: "error"));
        r = await _client.GetAsync($"/v1/image?src={Uri.EscapeDataString(missing)}");
        Assert.Equal(HttpStatusCode.BadGateway, r.StatusCode);
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        Assert.Equal("failed", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Signing_is_enforced_when_a_key_is_configured()
    {
        await DisposeAsync();
        await StartAsync(Options(signingKey: "k"));
        var unsigned = await _client.GetAsync($"/v1/image?src={Uri.EscapeDataString(Url)}");
        Assert.Equal(HttpStatusCode.Forbidden, unsigned.StatusCode);
        var sig = RequestSigning.Sign(Url, "k");
        var signed = await _client.GetAsync($"/v1/image?src={Uri.EscapeDataString(Url)}&sig={sig}");
        Assert.Equal(HttpStatusCode.OK, signed.StatusCode);
    }

    [Fact]
    public async Task Missing_src_unknown_recipe_and_disallowed_host_are_client_errors_or_failures()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/v1/image")).StatusCode);

        // A failed pipeline result on an allowlisted source still uses the ordinary redirect fallback.
        var unknownRecipe = await _client.GetAsync($"/v1/image?src={Uri.EscapeDataString(Url)}&recipe=nope");
        Assert.Equal(HttpStatusCode.Found, unknownRecipe.StatusCode);

        // A disallowed host must never be handed back as a redirect target: reject before the pipeline runs.
        var evil = Uri.EscapeDataString("https://evil.com/a.jpg");
        var off = await _client.GetAsync($"/v1/image?src={evil}");
        Assert.Equal(HttpStatusCode.BadRequest, off.StatusCode);
        using (var doc = JsonDocument.Parse(await off.Content.ReadAsStringAsync()))
            Assert.Equal("source not allowed", doc.RootElement.GetProperty("error").GetString());

        var offInspect = await _client.GetAsync($"/v1/inspect?src={evil}");
        Assert.Equal(HttpStatusCode.BadRequest, offInspect.StatusCode);
    }

    [Fact]
    public async Task Inspect_returns_the_record_and_normalize_accepts_raw_bytes()
    {
        var inspect = await _client.GetAsync($"/v1/inspect?src={Uri.EscapeDataString(Url)}");
        Assert.Equal(HttpStatusCode.OK, inspect.StatusCode);
        using var doc = JsonDocument.Parse(await inspect.Content.ReadAsStringAsync());
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.True(doc.RootElement.GetProperty("verdict").GetProperty("packShot").GetBoolean());

        var post = await _client.PostAsync("/v1/normalize", new ByteArrayContent(Shot()));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        Assert.Equal("image/webp", post.Content.Headers.ContentType!.MediaType);
        Assert.Equal("ok", post.Headers.GetValues("X-Plinth-Status").Single());

        var empty = await _client.PostAsync("/v1/normalize", new ByteArrayContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var junk = await _client.PostAsync("/v1/normalize", new ByteArrayContent("nope"u8.ToArray()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, junk.StatusCode);
    }

    [Fact]
    public async Task Normalize_rejects_a_body_larger_than_the_fetch_policy_allows()
    {
        await DisposeAsync();
        var capped = new PipelineOptions(
            new FetchPolicy(FetchPolicy.FromHostList("cdn.example.com").AllowedHosts, MaxBytes: 1024),
            "none", RecipeCatalog.DefaultOnly, null, "redirect", 1);
        await StartAsync(capped);

        var tooLarge = await _client.PostAsync("/v1/normalize", new ByteArrayContent(new byte[2048]));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        using var doc = JsonDocument.Parse(await tooLarge.Content.ReadAsStringAsync());
        Assert.Equal("body too large", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Health_and_version()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/healthz")).StatusCode);
        using var doc = JsonDocument.Parse(await _client.GetStringAsync("/version"));
        Assert.Equal(Engine.Version, doc.RootElement.GetProperty("engineVersion").GetString());
        Assert.StartsWith("8.", doc.RootElement.GetProperty("libvipsVersion").GetString());
        Assert.Equal("default", doc.RootElement.GetProperty("recipes")[0].GetString());
    }
}
