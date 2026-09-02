using System.Collections.Concurrent;
using Plinth.Core;

namespace Plinth.Pipeline.Stores;

/// <summary>In-process store for tests and single-run tooling.</summary>
public sealed class MemoryStore : IOutputStore
{
    private readonly ConcurrentDictionary<string, StoredOutput> _items = new();

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(_items.ContainsKey(key));

    public Task<StoredOutput?> TryGetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_items.TryGetValue(key, out var v) ? v : null);

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
