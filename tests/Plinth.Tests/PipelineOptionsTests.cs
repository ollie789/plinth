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
        };
        var o = PipelineOptions.FromEnvironment(k => env.GetValueOrDefault(k));
        Assert.Equal(new HashSet<string> { "a.com", "b.com" }, o.Fetch.AllowedHosts);
        Assert.Equal("fs:///tmp/x", o.StoreUri);
        Assert.Equal(["default", "tall"], o.Recipes.Names);
        Assert.Equal("secret", o.SigningKey);
        Assert.Equal("error", o.OnFailure);
        Assert.Equal(2, o.Concurrency);

        var bare = PipelineOptions.FromEnvironment(_ => null);
        Assert.Empty(bare.Fetch.AllowedHosts);
        Assert.Equal("none", bare.StoreUri);
        Assert.Equal(["default"], bare.Recipes.Names);
        Assert.Null(bare.SigningKey);
        Assert.Equal("redirect", bare.OnFailure);
        Assert.Null(bare.Concurrency);

        Assert.Throws<PlinthException>(() => PipelineOptions.FromEnvironment(k => k == "PLINTH_ON_FAILURE" ? "explode" : null));
    }
}
