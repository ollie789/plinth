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
}
