using Plinth.Core;
using Plinth.Pipeline.Stores;

namespace Plinth.Tests.Stores;

public class NullStoreTests
{
    [Fact]
    public async Task Failed_records_are_rejected_and_valid_puts_are_forgotten()
    {
        var store = new NullStore();
        var failed = ResultRecord.Failed(new string('d', 64), "https://x/d.jpg", Recipe.Default, "nope");
        await Assert.ThrowsAsync<ArgumentException>(() => store.PutAsync(failed.Key, [], failed));

        var r = Normalizer.Normalize(
            Synthetic.PackShot(800, 1000, Rgb.Parse("#ffffff"), 200, 300, 300, 400, Rgb.Parse("#000000")),
            Recipe.Default,
            "https://x/a.jpg");
        await store.PutAsync(r.Record.Key, r.Output!, r.Record);
        Assert.False(await store.ExistsAsync(r.Record.Key));
        Assert.Null(await store.TryGetAsync(r.Record.Key));
        Assert.Null(await store.TryGetRecordAsync(r.Record.Key));
    }
}
