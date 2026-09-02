using Plinth.Core;
using Plinth.Pipeline.Stores;

namespace Plinth.Tests.Stores;

/// <summary>Behaviour every IOutputStore must have. Subclass per implementation.</summary>
public abstract class OutputStoreContract
{
    protected abstract IOutputStore Create();

    private static readonly Rgb White = Rgb.Parse("#ffffff");
    private static readonly Rgb Black = Rgb.Parse("#000000");

    protected static NormalizeResult Sample() =>
        Normalizer.Normalize(Synthetic.PackShot(800, 1000, White, 200, 300, 300, 400, Black), Recipe.Default, "https://x/a.jpg");

    [Fact]
    public async Task Missing_key_is_absent()
    {
        var store = Create();
        Assert.False(await store.ExistsAsync(new string('b', 64)));
        Assert.Null(await store.TryGetAsync(new string('b', 64)));
    }

    [Fact]
    public async Task Put_then_get_round_trips_bytes_and_record()
    {
        var store = Create();
        var r = Sample();
        await store.PutAsync(r.Record.Key, r.Output!, r.Record);
        Assert.True(await store.ExistsAsync(r.Record.Key));
        var got = await store.TryGetAsync(r.Record.Key);
        Assert.NotNull(got);
        Assert.Equal(r.Output, got!.Bytes);
        Assert.Equal(r.Record.ToJson(), got.Record.ToJson());
    }

    [Fact]
    public async Task Record_only_reads_return_the_record_and_nothing_for_a_missing_key()
    {
        var store = Create();
        Assert.Null(await store.TryGetRecordAsync(new string('b', 64)));
        var r = Sample();
        await store.PutAsync(r.Record.Key, r.Output!, r.Record);
        var got = await store.TryGetRecordAsync(r.Record.Key);
        Assert.NotNull(got);
        Assert.Equal(r.Record.ToJson(), got!.ToJson());
    }

    [Fact]
    public async Task Failed_records_are_never_stored()
    {
        var store = Create();
        var failed = ResultRecord.Failed(new string('c', 64), "https://x/c.jpg", Recipe.Default, "nope");
        await Assert.ThrowsAsync<ArgumentException>(() => store.PutAsync(failed.Key, [], failed));
    }
}

public class MemoryStoreTests : OutputStoreContract
{
    protected override IOutputStore Create() => new MemoryStore();
}
