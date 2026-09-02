using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Plinth.Core;

namespace Plinth.Pipeline.Stores;

/// <summary>One container, the store layout as blob names, immutable cache headers on every image.</summary>
public sealed class AzureBlobStore(BlobContainerClient container) : IOutputStore
{
    public const string CacheControl = "public, max-age=31536000, immutable";

    public static AzureBlobStore FromEnvironment(string containerName, Func<string, string?> env)
    {
        var conn = env("PLINTH_AZURE_STORAGE_CONNECTION");
        if (!string.IsNullOrEmpty(conn))
            return new AzureBlobStore(new BlobContainerClient(conn, containerName));
        var account = env("PLINTH_AZURE_STORAGE_ACCOUNT");
        if (!string.IsNullOrEmpty(account))
            return new AzureBlobStore(new BlobContainerClient(new Uri(new Uri(account), containerName), new DefaultAzureCredential()));
        throw new PlinthException("azblob store needs PLINTH_AZURE_STORAGE_CONNECTION or PLINTH_AZURE_STORAGE_ACCOUNT");
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default) =>
        await container.GetBlobClient(StoreLayout.RecordPath(key)).ExistsAsync(ct);

    public async Task<StoredOutput?> TryGetAsync(string key, CancellationToken ct = default)
    {
        var recordBlob = container.GetBlobClient(StoreLayout.RecordPath(key));
        ResultRecord record;
        try
        {
            var r = await recordBlob.DownloadContentAsync(ct);
            record = ResultRecord.FromJson(r.Value.Content.ToString());
        }
        catch (RequestFailedException e) when (e.Status == 404) { return null; }
        if (record.Output is null) return null;
        try
        {
            var img = await container.GetBlobClient(StoreLayout.ImagePath(key, record.Output.Format)).DownloadContentAsync(ct);
            return new StoredOutput(img.Value.Content.ToArray(), record);
        }
        catch (RequestFailedException e) when (e.Status == 404) { return null; }
    }

    public async Task PutAsync(string key, byte[] bytes, ResultRecord record, CancellationToken ct = default)
    {
        StoreGuard.RequireStorable(record);
        var image = container.GetBlobClient(StoreLayout.ImagePath(key, record.Output!.Format));
        await image.UploadAsync(new BinaryData(bytes), new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = ImageFormats.MimeTypeFor(record.Output.Format), CacheControl = CacheControl },
        }, ct);
        var json = container.GetBlobClient(StoreLayout.RecordPath(key));
        await json.UploadAsync(new BinaryData(record.ToJson()), new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json", CacheControl = CacheControl },
        }, ct);
    }
}
