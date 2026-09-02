using Plinth.Core;

namespace Plinth.Pipeline.Stores;

public sealed record StoredOutput(byte[] Bytes, ResultRecord Record);

public interface IOutputStore
{
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
    Task<StoredOutput?> TryGetAsync(string key, CancellationToken ct = default);
    Task PutAsync(string key, byte[] bytes, ResultRecord record, CancellationToken ct = default);
}

public static class StoreLayout
{
    public static string RecordPath(string key) => $"{key[..2]}/{key}.json";
    public static string ImagePath(string key, string format) => $"{key[..2]}/{key}.{ImageFormats.ExtensionFor(format)}";
}
