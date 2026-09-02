using System.Net;

namespace Plinth.Tests.Fakes;

/// <summary>Answers requests from a script keyed by absolute URL; records what was asked.</summary>
public sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _script = new();
    public List<string> Requested { get; } = [];

    public ScriptedHandler On(string url, Func<HttpResponseMessage> respond) { _script[url] = respond; return this; }

    public static HttpResponseMessage Bytes(byte[] body, string contentType = "image/jpeg", long? contentLength = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        if (contentLength is not null) r.Content.Headers.ContentLength = contentLength;
        return r;
    }

    public static HttpResponseMessage Redirect(string location, HttpStatusCode code = HttpStatusCode.Found)
    {
        var r = new HttpResponseMessage(code);
        r.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return r;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        Requested.Add(url);
        if (!_script.TryGetValue(url, out var respond))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        return Task.FromResult(respond());
    }
}
