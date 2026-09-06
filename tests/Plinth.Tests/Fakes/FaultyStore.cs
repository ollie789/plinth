using Plinth.Core;
using Plinth.Pipeline.Stores;

namespace Plinth.Tests.Fakes;

/// <summary>A MemoryStore that can be told to throw on reads, on writes, or both — a store having a bad day.</summary>
public sealed class FaultyStore(bool failReads = false, bool failWrites = false) : IOutputStore
{
    public MemoryStore Inner { get; } = new();

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Read(() => Inner.ExistsAsync(key, ct), ct);
    public Task<StoredOutput?> TryGetAsync(string key, CancellationToken ct = default) => Read(() => Inner.TryGetAsync(key, ct), ct);
    public Task<ResultRecord?> TryGetRecordAsync(string key, CancellationToken ct = default) => Read(() => Inner.TryGetRecordAsync(key, ct), ct);

    public Task PutAsync(string key, byte[] bytes, ResultRecord record, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (failWrites) throw new IOException("store is down (write)");
        return Inner.PutAsync(key, bytes, record, ct);
    }

    private Task<T> Read<T>(Func<Task<T>> call, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (failReads) throw new IOException("store is down (read)");
        return call();
    }
}
