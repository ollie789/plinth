using Plinth.Core;

namespace Plinth.Pipeline.Stores;

/// <summary>Sharded files under one root: &lt;root&gt;/&lt;key[0..2]&gt;/&lt;key&gt;.&lt;ext&gt; and .json.</summary>
public sealed class FileSystemStore(string root) : IOutputStore
{
    public string Root { get; } = Path.GetFullPath(root);

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(File.Exists(Path.Combine(Root, StoreLayout.RecordPath(key))));

    public async Task<StoredOutput?> TryGetAsync(string key, CancellationToken ct = default)
    {
        var recordPath = Path.Combine(Root, StoreLayout.RecordPath(key));
        if (!File.Exists(recordPath)) return null;
        var record = ResultRecord.FromJson(await File.ReadAllTextAsync(recordPath, ct));
        if (record.Output is null) return null;
        var imagePath = Path.Combine(Root, StoreLayout.ImagePath(key, record.Output.Format));
        if (!File.Exists(imagePath)) return null;
        return new StoredOutput(await File.ReadAllBytesAsync(imagePath, ct), record);
    }

    public async Task PutAsync(string key, byte[] bytes, ResultRecord record, CancellationToken ct = default)
    {
        StoreGuard.RequireStorable(record);
        var imagePath = Path.Combine(Root, StoreLayout.ImagePath(key, record.Output!.Format));
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        // Write to a temp name and rename so a reader never sees a half-written file.
        var tmp = imagePath + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes, ct);
        File.Move(tmp, imagePath, overwrite: true);
        var recordPath = Path.Combine(Root, StoreLayout.RecordPath(key));
        var tmpRecord = recordPath + ".tmp";
        await File.WriteAllTextAsync(tmpRecord, record.ToJson(), ct);
        File.Move(tmpRecord, recordPath, overwrite: true);
    }
}
