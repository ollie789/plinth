using Plinth.Core;
using Plinth.Pipeline.Fetch;

namespace Plinth.Tests.Fakes;

/// <summary>Returns canned bytes per URL; counts calls; throws PlinthException for unknown URLs.</summary>
public sealed class FakeFetcher : ISourceFetcher
{
    private readonly Dictionary<string, byte[]> _bodies = new();
    private int _calls;

    // The CLI's fetch stage runs several workers concurrently, so callers can genuinely
    // race here; Interlocked keeps the count accurate under real concurrent access.
    public int Calls => _calls;

    public FakeFetcher With(string url, byte[] body) { _bodies[url] = body; return this; }

    public Task<FetchResult> FetchAsync(string url, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _calls);
        ct.ThrowIfCancellationRequested();
        if (!_bodies.TryGetValue(url, out var body)) throw new PlinthException("source 404");
        return Task.FromResult(new FetchResult(body, url, "image/jpeg"));
    }
}
