using Plinth.Core;
using Plinth.Pipeline;

namespace Plinth.Tests;

public class RecipeCatalogTests
{
    [Fact]
    public void Default_is_always_present_and_names_resolve()
    {
        var c = RecipeCatalog.FromJson("{\"square\":{\"aspect\":\"1:1\",\"width\":800}}");
        Assert.Equal(["default", "square"], c.Names);
        Assert.Equal(Recipe.Default.Hash, c.Get(null).Hash);
        Assert.Equal(Recipe.Default.Hash, c.Get("").Hash);
        Assert.Equal(800, c.Get("square").CanvasHeight);
        Assert.Throws<PlinthException>(() => c.Get("nope"));
        Assert.Throws<PlinthException>(() => RecipeCatalog.FromJson("[1]"));
        Assert.Throws<PlinthException>(() => RecipeCatalog.FromJson("{\"bad\":{\"format\":\"gif\"}}"));
    }
}
