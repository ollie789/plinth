namespace Plinth.Pipeline.Fetch;

public sealed record FetchResult(byte[] Bytes, string FinalUrl, string? ContentType);

public interface ISourceFetcher
{
    /// <summary>Fetch under the policy. Throws PlinthException for every refusal or failure.</summary>
    Task<FetchResult> FetchAsync(string url, CancellationToken ct = default);
}
