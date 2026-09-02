namespace Plinth.Tests;

public class FixturesTests
{
    [Fact]
    public void At_least_nine_engine_host_fixtures_are_present_and_small()
    {
        var all = Fixtures.All().ToList();
        Assert.True(all.Count >= 9, $"only {all.Count} fixtures");
        Assert.All(all, f => Assert.InRange(f.Bytes.Length, 1_000, 1_000_000));
    }
}
