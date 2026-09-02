using Plinth.Core;

namespace Plinth.Pipeline.Stores;

public sealed record StoredOutput(byte[] Bytes, ResultRecord Record);

public interface IOutputStore
{
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
    Task<StoredOutput?> TryGetAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// The record alone. `/v1/inspect` and any caller that only wants the verdict or the
    /// timings should use this: on a blob store, pulling the image too would be a needless
    /// download of the one part of the pair nobody is going to look at.
    /// </summary>
    Task<ResultRecord?> TryGetRecordAsync(string key, CancellationToken ct = default);
    Task PutAsync(string key, byte[] bytes, ResultRecord record, CancellationToken ct = default);
}

public static class StoreLayout
{
    public static string RecordPath(string key) => $"{Validated(key)[..2]}/{key}.json";
    public static string ImagePath(string key, string format) => $"{Validated(key)[..2]}/{key}.{ImageFormats.ExtensionFor(format)}";

    /// <summary>
    /// Every key this layout shards is a sha256 in lowercase hex. Checking that here keeps a
    /// key that came from somewhere else — a caller's string, a path fragment — from ever
    /// becoming a directory traversal or a stray file outside the shard scheme.
    /// </summary>
    private static string Validated(string key)
    {
        const string what = "an output key must be 64 lowercase hex characters";
        if (key is not { Length: 64 }) throw new ArgumentException(what, nameof(key));
        foreach (var c in key)
            if (!char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f'))
                throw new ArgumentException(what, nameof(key));
        return key;
    }
}
