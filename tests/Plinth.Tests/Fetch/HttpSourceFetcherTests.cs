using System.Net;
using Plinth.Core;
using Plinth.Pipeline.Fetch;
using Plinth.Tests.Fakes;

namespace Plinth.Tests.Fetch;

public class HttpSourceFetcherTests
{
    private static readonly FetchPolicy Policy = FetchPolicy.FromHostList("img1.theiconic.com.au, cdn.example.com");
    private static readonly byte[] Body = [1, 2, 3, 4];

    private static HttpSourceFetcher Fetcher(ScriptedHandler h, FetchPolicy? p = null) => new(p ?? Policy, h);

    [Fact]
    public void Policy_parses_hosts_and_matches_exactly_over_https_only()
    {
        Assert.True(Policy.Allows(new Uri("https://cdn.example.com/a.jpg")));
        Assert.False(Policy.Allows(new Uri("http://cdn.example.com/a.jpg")));
        Assert.False(Policy.Allows(new Uri("https://sub.cdn.example.com/a.jpg")));
        Assert.False(Policy.Allows(new Uri("https://evil.com/cdn.example.com")));
        Assert.Empty(FetchPolicy.FromHostList(null).AllowedHosts);
    }

    [Fact]
    public async Task Allowlisted_source_is_fetched()
    {
        var h = new ScriptedHandler().On("https://cdn.example.com/a.jpg", () => ScriptedHandler.Bytes(Body));
        var r = await Fetcher(h).FetchAsync("https://cdn.example.com/a.jpg");
        Assert.Equal(Body, r.Bytes);
        Assert.Equal("image/jpeg", r.ContentType);
        Assert.Single(h.Requested);
    }

    [Fact]
    public async Task Unlisted_host_or_http_is_refused_before_any_request()
    {
        var h = new ScriptedHandler();
        await Assert.ThrowsAsync<PlinthException>(() => Fetcher(h).FetchAsync("https://other.com/a.jpg"));
        await Assert.ThrowsAsync<PlinthException>(() => Fetcher(h).FetchAsync("http://cdn.example.com/a.jpg"));
        await Assert.ThrowsAsync<PlinthException>(() => Fetcher(h, FetchPolicy.FromHostList("")).FetchAsync("https://cdn.example.com/a.jpg"));
        Assert.Empty(h.Requested);
    }

    [Fact]
    public async Task Redirects_are_followed_only_within_the_allowlist_and_at_most_three_times()
    {
        var ok = new ScriptedHandler()
            .On("https://cdn.example.com/a", () => ScriptedHandler.Redirect("https://img1.theiconic.com.au/b"))
            .On("https://img1.theiconic.com.au/b", () => ScriptedHandler.Redirect("/c"))
            .On("https://img1.theiconic.com.au/c", () => ScriptedHandler.Bytes(Body));
        var r = await Fetcher(ok).FetchAsync("https://cdn.example.com/a");
        Assert.Equal("https://img1.theiconic.com.au/c", r.FinalUrl);

        var offList = new ScriptedHandler().On("https://cdn.example.com/a", () => ScriptedHandler.Redirect("https://evil.com/x"));
        await Assert.ThrowsAsync<PlinthException>(() => Fetcher(offList).FetchAsync("https://cdn.example.com/a"));
        Assert.Single(offList.Requested);

        var loop = new ScriptedHandler()
            .On("https://cdn.example.com/1", () => ScriptedHandler.Redirect("https://cdn.example.com/2"))
            .On("https://cdn.example.com/2", () => ScriptedHandler.Redirect("https://cdn.example.com/3"))
            .On("https://cdn.example.com/3", () => ScriptedHandler.Redirect("https://cdn.example.com/4"))
            .On("https://cdn.example.com/4", () => ScriptedHandler.Redirect("https://cdn.example.com/5"))
            .On("https://cdn.example.com/5", () => ScriptedHandler.Bytes(Body));
        await Assert.ThrowsAsync<PlinthException>(() => Fetcher(loop).FetchAsync("https://cdn.example.com/1"));
        Assert.Equal(4, loop.Requested.Count);
    }

    [Fact]
    public async Task Bodies_over_the_cap_are_refused_by_header_and_by_stream()
    {
        var small = new FetchPolicy(Policy.AllowedHosts, MaxBytes: 3);
        var byHeader = new ScriptedHandler().On("https://cdn.example.com/a.jpg", () => ScriptedHandler.Bytes(Body, contentLength: 4));
        var ex = await Assert.ThrowsAsync<PlinthException>(() => Fetcher(byHeader, small).FetchAsync("https://cdn.example.com/a.jpg"));
        Assert.Contains("too large", ex.Message);

        var byStream = new ScriptedHandler().On("https://cdn.example.com/a.jpg", () =>
        {
            var r = ScriptedHandler.Bytes(Body);
            r.Content.Headers.ContentLength = null;
            return r;
        });
        ex = await Assert.ThrowsAsync<PlinthException>(() => Fetcher(byStream, small).FetchAsync("https://cdn.example.com/a.jpg"));
        Assert.Contains("too large", ex.Message);
    }

    [Fact]
    public async Task Non_success_status_is_a_plinth_failure_with_the_status()
    {
        var h = new ScriptedHandler().On("https://cdn.example.com/a.jpg", () => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var ex = await Assert.ThrowsAsync<PlinthException>(() => Fetcher(h).FetchAsync("https://cdn.example.com/a.jpg"));
        Assert.Contains("403", ex.Message);
    }

    [Fact]
    public async Task Real_handler_refuses_a_host_that_resolves_privately()
    {
        // localhost is never on a real allowlist; put it on one to prove the connect-time block fires.
        var f = new HttpSourceFetcher(FetchPolicy.FromHostList("localhost"));
        var ex = await Assert.ThrowsAsync<PlinthException>(() => f.FetchAsync("https://localhost/x.jpg"));
        Assert.Contains("blocked address", ex.Message);
    }

    [Fact]
    public async Task A_stream_that_fails_mid_body_is_a_plinth_failure()
    {
        var h = new ScriptedHandler().On("https://cdn.example.com/a.jpg", () =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FlakyStream([1, 2])) };
            r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            return r;
        });
        var ex = await Assert.ThrowsAsync<PlinthException>(() => Fetcher(h).FetchAsync("https://cdn.example.com/a.jpg"));
        Assert.Contains("read failed", ex.Message);
    }

    [Fact]
    public async Task Caller_cancellation_surfaces_as_cancellation()
    {
        var h = new ScriptedHandler().On("https://cdn.example.com/a.jpg", () => ScriptedHandler.Bytes(Body));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Fetcher(h).FetchAsync("https://cdn.example.com/a.jpg", cts.Token));
    }

    /// <summary>Serves a few bytes then throws IOException, as a reset connection would mid-download.</summary>
    private sealed class FlakyStream(byte[] firstChunk) : Stream
    {
        private bool _served;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_served) throw new IOException("connection reset by peer");
            _served = true;
            firstChunk.CopyTo(buffer);
            return ValueTask.FromResult(firstChunk.Length);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
