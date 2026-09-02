using Plinth.Core;

namespace Plinth.Tests;

public class ResultRecordTests
{
    [Fact]
    public void Failed_factory_builds_a_complete_failed_record()
    {
        var r = ResultRecord.Failed("k", "https://a/b.jpg", Recipe.Default, "source 404");
        Assert.Equal("failed", r.Status);
        Assert.Equal("source 404", r.Error);
        Assert.Equal("k", r.Key);
        Assert.Equal("https://a/b.jpg", r.SourceId);
        Assert.Equal(Engine.Version, r.EngineVersion);
        Assert.Equal(Recipe.Default.Hash, r.RecipeHash);
        Assert.Null(r.Output);
        var back = ResultRecord.FromJson(r.ToJson());
        Assert.Equal("failed", back.Status);
        Assert.Null(back.Output);
    }
}
