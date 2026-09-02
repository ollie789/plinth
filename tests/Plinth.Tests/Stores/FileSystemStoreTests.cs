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

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
