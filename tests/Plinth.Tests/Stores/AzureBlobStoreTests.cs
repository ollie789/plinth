using Azure.Storage.Blobs;
using Plinth.Core;
using Plinth.Pipeline.Stores;

namespace Plinth.Tests.Stores;

public class AzureBlobStoreTests
{
    [Fact]
    public void Blob_names_follow_the_store_layout_and_headers_are_immutable()
    {
        Assert.Equal("public, max-age=31536000, immutable", AzureBlobStore.CacheControl);
        var key = new string('d', 64);
        Assert.Equal($"dd/{key}.webp", StoreLayout.ImagePath(key, "webp"));
    }

    [AzureFact]
    public async Task Round_trips_against_a_real_container()
    {
        var conn = Environment.GetEnvironmentVariable("PLINTH_TEST_AZURE_CONNECTION")!;
        var container = new BlobContainerClient(conn, "plinth-test-" + Guid.NewGuid().ToString("N")[..8]);
        await container.CreateIfNotExistsAsync();
        try
        {
            var store = new AzureBlobStore(container);
            var r = Normalizer.Normalize(Synthetic.PackShot(800, 1000, Rgb.Parse("#ffffff"), 200, 300, 300, 400, Rgb.Parse("#000000")), Recipe.Default, "https://x/a.jpg");
            Assert.False(await store.ExistsAsync(r.Record.Key));
            await store.PutAsync(r.Record.Key, r.Output!, r.Record);
            Assert.True(await store.ExistsAsync(r.Record.Key));
            var got = await store.TryGetAsync(r.Record.Key);
            Assert.Equal(r.Output, got!.Bytes);
            var props = await container.GetBlobClient(StoreLayout.ImagePath(r.Record.Key, "webp")).GetPropertiesAsync();
            Assert.Equal("image/webp", props.Value.ContentType);
            Assert.Equal(AzureBlobStore.CacheControl, props.Value.CacheControl);
        }
        finally { await container.DeleteIfExistsAsync(); }
    }
}
