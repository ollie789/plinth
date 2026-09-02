using Plinth.Pipeline.Stores;

namespace Plinth.Tests.Stores;

public class FileSystemStoreTests : OutputStoreContract, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "plinth-fs-" + Guid.NewGuid().ToString("N"));

    protected override IOutputStore Create() => new FileSystemStore(_root);

    [Fact]
    public async Task Files_land_in_the_sharded_layout()
    {
        var store = Create();
        var r = Sample();
        await store.PutAsync(r.Record.Key, r.Output!, r.Record);
        Assert.True(File.Exists(Path.Combine(_root, StoreLayout.ImagePath(r.Record.Key, "webp"))));
        Assert.True(File.Exists(Path.Combine(_root, StoreLayout.RecordPath(r.Record.Key))));
    }

    [Fact]
    public async Task Concurrent_puts_of_the_same_key_all_succeed()
    {
        var store = Create();
        var r = Sample();
        var puts = Enumerable.Range(0, 8).Select(_ => store.PutAsync(r.Record.Key, r.Output!, r.Record));
        await Task.WhenAll(puts);

        var got = await store.TryGetAsync(r.Record.Key);
        Assert.NotNull(got);
        Assert.Equal(r.Output, got!.Bytes);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
