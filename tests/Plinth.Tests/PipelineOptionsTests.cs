using Plinth.Core;
using Plinth.Pipeline;

namespace Plinth.Tests;

public class PipelineOptionsTests
{
    [Fact]
    public void Reads_every_variable_with_sane_defaults()
    {
        var recipes = Path.GetTempFileName();
        File.WriteAllText(recipes, "{\"tall\":{\"aspect\":\"2:3\"}}");
        var env = new Dictionary<string, string?>
        {
            ["PLINTH_ALLOWED_HOSTS"] = "a.com, B.com",
            ["PLINTH_STORE"] = "fs:///tmp/x",
            ["PLINTH_RECIPES"] = recipes,
            ["PLINTH_SIGNING_KEY"] = "secret",
            ["PLINTH_ON_FAILURE"] = "error",
            ["PLINTH_CONCURRENCY"] = "2",
            ["PLINTH_MAX_INFLIGHT"] = "9",
            ["PLINTH_ENABLE_NORMALIZE"] = "true",
            ["PLINTH_MAX_PIXELS"] = "20000000",
        };
        var o = PipelineOptions.FromEnvironment(k => env.GetValueOrDefault(k));
        Assert.Equal(new HashSet<string> { "a.com", "b.com" }, o.Fetch.AllowedHosts);
        Assert.Equal("fs:///tmp/x", o.StoreUri);
        Assert.Equal(["default", "tall"], o.Recipes.Names);
        Assert.Equal("secret", o.SigningKey);
        Assert.Equal("error", o.OnFailure);
        Assert.Equal(2, o.Concurrency);
        Assert.Equal(9, o.MaxInFlight);
        Assert.True(o.NormalizeEnabled);
        Assert.Equal(20_000_000, o.MaxPixels);
        Assert.Equal(20 * 1024 * 1024, o.Fetch.MaxBytes);

        var bare = PipelineOptions.FromEnvironment(_ => null);
        Assert.Empty(bare.Fetch.AllowedHosts);
        Assert.Equal("none", bare.StoreUri);
        Assert.Equal(["default"], bare.Recipes.Names);
        Assert.Null(bare.SigningKey);
        Assert.Equal("redirect", bare.OnFailure);
        Assert.Null(bare.Concurrency);
        Assert.Equal(4, bare.MaxInFlight);

        Assert.Throws<PlinthException>(() => PipelineOptions.FromEnvironment(k => k == "PLINTH_ON_FAILURE" ? "explode" : null));
        Assert.Throws<PlinthException>(() => PipelineOptions.FromEnvironment(k => k == "PLINTH_MAX_INFLIGHT" ? "0" : null));
        Assert.Throws<PlinthException>(() => PipelineOptions.FromEnvironment(k => k == "PLINTH_MAX_INFLIGHT" ? "lots" : null));
        Assert.Throws<PlinthException>(() => PipelineOptions.FromEnvironment(k => k == "PLINTH_MAX_BYTES" ? "0" : null));
        Assert.Throws<PlinthException>(() => PipelineOptions.FromEnvironment(k => k == "PLINTH_MAX_BYTES" ? "plenty" : null));
        Assert.Throws<PlinthException>(() => PipelineOptions.FromEnvironment(k => k == "PLINTH_MAX_PIXELS" ? "0" : null));
        Assert.Equal(SourceInspector.MaxPixels, PipelineOptions.FromEnvironment(_ => null).MaxPixels);

        var missing = Path.Combine(Path.GetTempPath(), "plinth-no-such-recipes-" + Guid.NewGuid().ToString("N") + ".json");
        var unreadable = Assert.Throws<PlinthException>(() => PipelineOptions.FromEnvironment(k => k == "PLINTH_RECIPES" ? missing : null));
        Assert.Equal($"PLINTH_RECIPES: could not read {missing}", unreadable.Message);
    }

    [Fact]
    public void The_upload_route_is_off_unless_asked_for_by_exactly_true()
    {
        Assert.False(PipelineOptions.FromEnvironment(_ => null).NormalizeEnabled);
        Assert.False(PipelineOptions.FromEnvironment(k => k == "PLINTH_ENABLE_NORMALIZE" ? "1" : null).NormalizeEnabled);
        Assert.True(PipelineOptions.FromEnvironment(k => k == "PLINTH_ENABLE_NORMALIZE" ? "true" : null).NormalizeEnabled);
    }

    [Fact]
    public void The_fetch_cap_is_settable_per_deployment()
    {
        // A provider re-hosting 16 MB thumbnails should not require a release.
        var o = PipelineOptions.FromEnvironment(k => k == "PLINTH_MAX_BYTES" ? "33554432" : null);
        Assert.Equal(32 * 1024 * 1024, o.Fetch.MaxBytes);
    }
}
