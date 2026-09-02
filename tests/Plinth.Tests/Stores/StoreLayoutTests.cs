using Plinth.Pipeline.Stores;

namespace Plinth.Tests.Stores;

public class StoreLayoutTests
{
    [Fact]
    public void Paths_shard_by_the_first_two_key_characters()
    {
        var key = new string('a', 64);
        Assert.Equal($"aa/{key}.json", StoreLayout.RecordPath(key));
        Assert.Equal($"aa/{key}.webp", StoreLayout.ImagePath(key, "webp"));
        Assert.Equal($"aa/{key}.jpg", StoreLayout.ImagePath(key, "jpeg"));
    }
}
