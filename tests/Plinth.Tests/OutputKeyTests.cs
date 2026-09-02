using System.Security.Cryptography;
using System.Text;
using Plinth.Core;

namespace Plinth.Tests;

public class OutputKeyTests
{
    [Fact]
    public void Key_is_sha256_of_sourceId_recipeHash_and_version()
    {
        var src = "https://img1.theiconic.com.au/a.jpg";
        var expected = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{src}|{Recipe.Default.Hash}|{Engine.Version}")));
        Assert.Equal(expected, OutputKey.Compute(src, Recipe.Default));
        Assert.Equal(64, expected.Length);
    }

    [Fact]
    public void Key_changes_with_source_recipe_or_version()
    {
        var a = OutputKey.Compute("https://a/x.jpg", Recipe.Default);
        Assert.NotEqual(a, OutputKey.Compute("https://a/y.jpg", Recipe.Default));
        Assert.NotEqual(a, OutputKey.Compute("https://a/x.jpg", Recipe.Default with { Quality = 1 }));
        Assert.NotEqual(a, OutputKey.Compute("https://a/x.jpg", Recipe.Default.Hash, "9.9"));
    }
}
