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

    [Theory]
    [InlineData("")]
    [InlineData("aa")]
    [InlineData("../../etc/passwd")]
    public void A_key_that_is_not_64_lowercase_hex_characters_is_refused(string key)
    {
        Assert.Throws<ArgumentException>(() => StoreLayout.RecordPath(key));
        Assert.Throws<ArgumentException>(() => StoreLayout.ImagePath(key, "webp"));
    }

    [Fact]
    public void Uppercase_and_non_hex_keys_of_the_right_length_are_refused_too()
    {
        Assert.Throws<ArgumentException>(() => StoreLayout.RecordPath(new string('A', 64)));
        Assert.Throws<ArgumentException>(() => StoreLayout.RecordPath(new string('z', 64)));
    }
}
