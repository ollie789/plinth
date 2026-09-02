using System.Collections.Concurrent;
using Plinth.Core;

namespace Plinth.Pipeline.Stores;

/// <summary>In-process store for tests and single-run tooling.</summary>
public sealed class MemoryStore : IOutputStore
{
    private readonly ConcurrentDictionary<string, StoredOutput> _items = new();
    private int _tryGetCalls;
    private int _tryGetRecordCalls;

    /// <summary>How many times the image-and-record pair was asked for. Tests assert on this.</summary>
    public int TryGetCalls => _tryGetCalls;

    /// <summary>How many times only the record was asked for. Tests assert on this.</summary>
    public int TryGetRecordCalls => _tryGetRecordCalls;

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(_items.ContainsKey(key));

    public Task<StoredOutput?> TryGetAsync(string key, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _tryGetCalls);
        return Task.FromResult(_items.TryGetValue(key, out var v) ? v : null);
    }

    public Task<ResultRecord?> TryGetRecordAsync(string key, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _tryGetRecordCalls);
        return Task.FromResult(_items.TryGetValue(key, out var v) ? v.Record : null);
    }

    public Task PutAsync(string key, byte[] bytes, ResultRecord record, CancellationToken ct = default)
    {
        StoreGuard.RequireStorable(record);
        _items[key] = new StoredOutput(bytes, record);
        return Task.CompletedTask;
    }
}

internal static class StoreGuard
{
    public static void RequireStorable(ResultRecord record)
    {
        if (record.Output is null || record.Status == "failed")
            throw new ArgumentException("failed records are never stored", nameof(record));
    }
}
