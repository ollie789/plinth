using Plinth.Pipeline.Fetch;

namespace Plinth.Tests;

public class SourceUpgradesTests
{
    [Fact]
    public void An_amazon_size_token_is_dropped_to_reach_the_master()
    {
        Assert.Equal("https://m.media-amazon.com/images/I/71abcDEF.jpg",
            SourceUpgrades.Apply("https://m.media-amazon.com/images/I/71abcDEF._AC_SY445_.jpg"));
        // The tokens vary in shape: underscores, digits and commas all appear.
        Assert.Equal("https://m.media-amazon.com/images/I/71abcDEF.jpg",
            SourceUpgrades.Apply("https://m.media-amazon.com/images/I/71abcDEF._SX679_.jpg"));
        Assert.Equal("https://m.media-amazon.com/images/I/71abcDEF.png",
            SourceUpgrades.Apply("https://m.media-amazon.com/images/I/71abcDEF._AC_SR38,50_.png"));
    }

    [Fact]
    public void An_amazon_url_that_is_already_the_master_is_left_alone()
    {
        const string master = "https://m.media-amazon.com/images/I/71abcDEF.jpg";
        Assert.Equal(master, SourceUpgrades.Apply(master));
        // A dot inside the id is not a size token: no trailing _._ pair.
        const string dotted = "https://m.media-amazon.com/images/I/71abc.DEF.jpg";
        Assert.Equal(dotted, SourceUpgrades.Apply(dotted));
    }

    [Fact]
    public void An_adidas_width_segment_is_raised_to_1200()
    {
        Assert.Equal("https://assets.adidas.com/images/w_1200,f_auto,q_auto/abc123/Shoes.jpg",
            SourceUpgrades.Apply("https://assets.adidas.com/images/w_500,f_auto,q_auto/abc123/Shoes.jpg"));
    }

    [Fact]
    public void An_adidas_url_without_the_width_segment_is_left_alone()
    {
        const string other = "https://assets.adidas.com/images/w_600,f_auto/abc123/Shoes.jpg";
        Assert.Equal(other, SourceUpgrades.Apply(other));
    }

    [Fact]
    public void An_unknown_host_and_a_non_url_come_back_unchanged()
    {
        // The same shapes, on a host with no rule.
        const string amazonish = "https://cdn.example.com/images/I/71abcDEF._AC_SY445_.jpg";
        Assert.Equal(amazonish, SourceUpgrades.Apply(amazonish));
        const string adidasish = "https://cdn.example.com/images/w_500,f_auto/a/Shoes.jpg";
        Assert.Equal(adidasish, SourceUpgrades.Apply(adidasish));
        Assert.Equal("not a url", SourceUpgrades.Apply("not a url"));
        Assert.Equal("", SourceUpgrades.Apply(""));
    }

    [Fact]
    public void A_query_string_and_fragment_survive_the_rewrite()
    {
        Assert.Equal("https://m.media-amazon.com/images/I/71abcDEF.jpg?v=2#top",
            SourceUpgrades.Apply("https://m.media-amazon.com/images/I/71abcDEF._AC_SY445_.jpg?v=2#top"));
    }
}
