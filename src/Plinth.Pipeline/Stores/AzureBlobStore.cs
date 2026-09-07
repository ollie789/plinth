using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Plinth.Core;

namespace Plinth.Pipeline.Stores;

/// <summary>One container, the store layout as blob names, immutable cache headers on every image.</summary>
public sealed class AzureBlobStore(BlobContainerClient container) : IOutputStore
{
    public static AzureBlobStore FromEnvironment(string containerName, Func<string, string?> env)
    {
        var conn = env("PLINTH_AZURE_STORAGE_CONNECTION");
        var account = env("PLINTH_AZURE_STORAGE_ACCOUNT");
        if (string.IsNullOrEmpty(conn) && string.IsNullOrEmpty(account))
            throw new PlinthException("azblob store needs PLINTH_AZURE_STORAGE_CONNECTION or PLINTH_AZURE_STORAGE_ACCOUNT");
        try
        {
            return !string.IsNullOrEmpty(conn)
                ? new AzureBlobStore(new BlobContainerClient(conn, containerName))
                : new AzureBlobStore(new BlobContainerClient(new Uri(new Uri(account!), containerName), new DefaultAzureCredential()));
        }
        catch (Exception e) when (e is not PlinthException)
        {
            // Deliberately not e.Message: the SDK quotes the connection string it choked on,
            // account key included, and this message ends up in logs and error responses.
            throw new PlinthException("azblob store configuration is invalid");
        }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default) =>
        await container.GetBlobClient(StoreLayout.RecordPath(key)).ExistsAsync(ct);

    public async Task<byte[]?> TryGetImageAsync(string key, string format, CancellationToken ct = default)
    {
        try
        {
            var img = await container.GetBlobClient(StoreLayout.ImagePath(key, format)).DownloadContentAsync(ct);
            return img.Value.Content.ToArray();
        }
        catch (RequestFailedException e) when (e.Status == 404) { return null; }
    }

    public async Task<ResultRecord?> TryGetRecordAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var r = await container.GetBlobClient(StoreLayout.RecordPath(key)).DownloadContentAsync(ct);
            return ResultRecord.FromJson(r.Value.Content.ToString());
        }
        catch (RequestFailedException e) when (e.Status == 404) { return null; }
    }

    public async Task PutAsync(string key, byte[] bytes, ResultRecord record, CancellationToken ct = default)
    {
        StoreGuard.RequireStorable(record);
        var image = container.GetBlobClient(StoreLayout.ImagePath(key, record.Output!.Format));
        await image.UploadAsync(new BinaryData(bytes), new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = ImageFormats.MimeTypeFor(record.Output.Format), CacheControl = StoreLayout.ImmutableCache },
        }, ct);
        var json = container.GetBlobClient(StoreLayout.RecordPath(key));
        await json.UploadAsync(new BinaryData(record.ToJson()), new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json", CacheControl = StoreLayout.ImmutableCache },
        }, ct);
    }
}
