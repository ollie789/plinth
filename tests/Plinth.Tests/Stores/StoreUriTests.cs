using Plinth.Core;
using Plinth.Pipeline.Stores;

namespace Plinth.Tests.Stores;

public class StoreUriTests
{
    private static string? NoEnv(string _) => null;

    [Fact]
    public void None_and_fs_open_the_right_stores()
    {
        Assert.IsType<NullStore>(StoreUri.Open("none", NoEnv));
        var fs = Assert.IsType<FileSystemStore>(StoreUri.Open("fs:///tmp/plinth-x", NoEnv));
        Assert.Equal(Path.GetFullPath("/tmp/plinth-x"), fs.Root);
        Assert.IsType<FileSystemStore>(StoreUri.Open("fs://relative/dir", NoEnv));
    }

    [Fact]
    public void Azblob_needs_credentials_and_unknown_schemes_are_refused()
    {
        Assert.Throws<PlinthException>(() => StoreUri.Open("azblob://tiles", NoEnv));
        Assert.Throws<PlinthException>(() => StoreUri.Open("s3://bucket", NoEnv));
        Assert.Throws<PlinthException>(() => StoreUri.Open("", NoEnv));
    }

    [Fact]
    public void A_malformed_azure_connection_string_fails_without_repeating_itself()
    {
        const string secret = "Endpoint=nonsense;AccountKey=hunter2";
        var e = Assert.Throws<PlinthException>(() =>
            StoreUri.Open("azblob://tiles", k => k == "PLINTH_AZURE_STORAGE_CONNECTION" ? secret : null));
        Assert.Equal("azblob store configuration is invalid", e.Message);
        Assert.DoesNotContain("hunter2", e.ToString(), StringComparison.Ordinal);
    }
}
