using Plinth.Core;

namespace Plinth.Tests;

public class SourceIdTests
{
    [Fact]
    public void Url_is_canonicalised_but_query_is_preserved()
    {
        Assert.Equal(
            "https://img1.theiconic.com.au/a/b.jpg?v=3",
            SourceId.FromUrl("HTTPS://IMG1.TheIconic.com.au:443/a/b.jpg?v=3#frag"));
    }

    [Fact]
    public void Url_must_be_https_and_absolute()
    {
        Assert.Throws<PlinthException>(() => SourceId.FromUrl("http://x.com/a.jpg"));
        Assert.Throws<PlinthException>(() => SourceId.FromUrl("/a.jpg"));
        Assert.Throws<PlinthException>(() => SourceId.FromUrl("not a url"));
    }

    [Fact]
    public void Bytes_id_is_sha256_prefixed()
    {
        var id = SourceId.FromBytes("hello"u8);
        Assert.Equal("sha256:2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", id);
    }

    [Fact]
    public void Equivalent_spellings_converge_to_the_same_id()
    {
        Assert.Equal(
            SourceId.FromUrl("https://example.com/a~b.jpg?x=_abc"),
            SourceId.FromUrl("https://example.com/a%7Eb.jpg?x=%5Fabc"));

        Assert.Equal(
            "https://example.com/b.jpg",
            SourceId.FromUrl("https://example.com/x/../b.jpg"));
    }

    [Fact]
    public void Reserved_escapes_and_path_case_survive()
    {
        Assert.Equal(
            "https://example.com/A/b%2Fc.jpg?q=a%26b&Z=1",
            SourceId.FromUrl("https://example.com/A/b%2Fc.jpg?q=a%26b&Z=1"));
    }
}
