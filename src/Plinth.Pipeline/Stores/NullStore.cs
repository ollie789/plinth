using Plinth.Core;

namespace Plinth.Pipeline.Stores;

/// <summary>Pass-through: nothing is remembered. The API's default when no store is configured.</summary>
public sealed class NullStore : IOutputStore
{
    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(false);
    public Task<StoredOutput?> TryGetAsync(string key, CancellationToken ct = default) => Task.FromResult<StoredOutput?>(null);

    public Task PutAsync(string key, byte[] bytes, ResultRecord record, CancellationToken ct = default)
    {
        StoreGuard.RequireStorable(record);
        return Task.CompletedTask;
    }
}
