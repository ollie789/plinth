using Plinth.Core;

namespace Plinth.Pipeline.Stores;

/// <summary>"none" | "fs://&lt;path&gt;" | "azblob://&lt;container&gt;" → a store.</summary>
public static class StoreUri
{
    public static IOutputStore Open(string uri, Func<string, string?> env)
    {
        if (string.IsNullOrWhiteSpace(uri)) throw new PlinthException("store URI is empty (use none, fs://<path> or azblob://<container>)");
        if (uri == "none") return new NullStore();
        if (uri.StartsWith("fs://", StringComparison.Ordinal))
        {
            var path = uri["fs://".Length..];
            if (path.Length == 0) throw new PlinthException("fs:// store needs a path");
            return new FileSystemStore(path);
        }
        if (uri.StartsWith("azblob://", StringComparison.Ordinal))
        {
            var containerName = uri["azblob://".Length..];
            if (containerName.Length == 0) throw new PlinthException("azblob:// store needs a container name");
            try { return AzureBlobStore.FromEnvironment(containerName, env); }
            catch (Exception e) when (e is not PlinthException) { throw new PlinthException("azblob store configuration is invalid"); }
        }
        throw new PlinthException($"unsupported store URI '{uri}'");
    }
}
