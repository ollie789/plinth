namespace Plinth.Api;

/// <summary>
/// Three numbers the platform can scrape without a metrics stack: how many responses came
/// from the store, how many were processed, how many failed. Counted with Interlocked because
/// every request thread touches them.
/// </summary>
public sealed class ApiStats
{
    private long _hits;
    private long _misses;
    private long _failed;

    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);
    public long Failed => Interlocked.Read(ref _failed);

    /// <summary>One pipeline result, counted once: failed, or else hit or miss.</summary>
    public void Observe(string status, bool fromStore)
    {
        if (status == "failed") Interlocked.Increment(ref _failed);
        else if (fromStore) Interlocked.Increment(ref _hits);
        else Interlocked.Increment(ref _misses);
    }
}
