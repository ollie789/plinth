using System.Net;
using System.Net.Sockets;
using Plinth.Core;

namespace Plinth.Pipeline.Fetch;

/// <summary>
/// The only thing in Plinth that opens a network connection. Redirects are
/// followed by hand so every hop is re-checked; the connect callback rejects
/// hosts that resolve to private addresses at connect time, which closes the
/// DNS-rebinding gap a pre-check would leave open.
/// </summary>
public sealed class HttpSourceFetcher : ISourceFetcher
{
    private readonly FetchPolicy _policy;
    private readonly HttpClient _http;

    public HttpSourceFetcher(FetchPolicy policy, HttpMessageHandler? handler = null)
    {
        _policy = policy;
        _http = new HttpClient(handler ?? GuardedHandler(), disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(policy.TimeoutSeconds),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(policy.UserAgent);
    }

    private static SocketsHttpHandler GuardedHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectCallback = async (ctx, ct) =>
        {
            var host = ctx.DnsEndPoint.Host;
            IPAddress[] addresses;
            try { addresses = await Dns.GetHostAddressesAsync(host, ct); }
            catch (SocketException e) { throw new PlinthException($"source host {host} did not resolve", e); }
            var allowed = addresses.Where(a => !PrivateAddress.IsBlocked(a)).ToArray();
            if (allowed.Length == 0) throw new PlinthException($"source host {host} resolves to a blocked address");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, ctx.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        },
    };

    public async Task<FetchResult> FetchAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !_policy.Allows(uri))
            throw new PlinthException($"source not allowed: {url}");

        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException e)
            {
                throw FindPlinthException(e) ?? new PlinthException($"source fetch failed: {e.Message}", e);
            }
            catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
            {
                throw new PlinthException("source fetch timed out", e);
            }

            using (response)
            {
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    if (hop >= _policy.MaxRedirects) throw new PlinthException("source redirected too many times");
                    var location = response.Headers.Location ?? throw new PlinthException("source redirect without a location");
                    var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    if (!_policy.Allows(next)) throw new PlinthException($"source redirected off the allowlist: {next}");
                    uri = next;
                    continue;
                }
                if (!response.IsSuccessStatusCode) throw new PlinthException($"source {status}");

                var declared = response.Content.Headers.ContentLength;
                if (declared > _policy.MaxBytes) throw new PlinthException("source too large");

                try
                {
                    var stream = await response.Content.ReadAsStreamAsync(ct);
                    await using (stream)
                    {
                        var buffer = new MemoryStream(capacity: (int)Math.Min(declared ?? 256 * 1024, _policy.MaxBytes));
                        var chunk = new byte[64 * 1024];
                        int read;
                        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
                        {
                            if (buffer.Length + read > _policy.MaxBytes) throw new PlinthException("source too large");
                            buffer.Write(chunk, 0, read);
                        }
                        return new FetchResult(buffer.ToArray(), uri.ToString(), response.Content.Headers.ContentType?.MediaType);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
                {
                    throw new PlinthException("source fetch timed out", e);
                }
                catch (IOException e)
                {
                    throw new PlinthException($"source read failed: {e.Message}", e);
                }
                catch (HttpRequestException e)
                {
                    throw new PlinthException($"source read failed: {e.Message}", e);
                }
            }
        }
    }

    /// <summary>
    /// The ConnectCallback's PlinthException can arrive nested arbitrarily deep inside
    /// HttpRequestException.InnerException chains (SocketsHttpHandler wraps connect
    /// failures, and sometimes wraps its own wrapper). Walk until we find it or run out.
    /// </summary>
    private static PlinthException? FindPlinthException(Exception e)
    {
        for (var current = (Exception?)e; current is not null; current = current.InnerException)
            if (current is PlinthException pe) return pe;
        return null;
    }
}
