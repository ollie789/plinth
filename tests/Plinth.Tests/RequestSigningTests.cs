using System.Security.Cryptography;
using System.Text;
using Plinth.Pipeline;

namespace Plinth.Tests;

public class RequestSigningTests
{
    [Fact]
    public void Sign_is_hmac_sha256_hex_and_verify_is_exact()
    {
        var expected = Convert.ToHexStringLower(HMACSHA256.HashData("k"u8.ToArray(), Encoding.UTF8.GetBytes("https://a/b.jpg")));
        var sig = RequestSigning.Sign("https://a/b.jpg", "k");
        Assert.Equal(expected, sig);
        Assert.True(RequestSigning.Verify("https://a/b.jpg", sig, "k"));
        Assert.True(RequestSigning.Verify("https://a/b.jpg", sig.ToUpperInvariant(), "k"));
        Assert.False(RequestSigning.Verify("https://a/b.jpg", sig, "other"));
        Assert.False(RequestSigning.Verify("https://a/c.jpg", sig, "k"));
        Assert.False(RequestSigning.Verify("https://a/b.jpg", null, "k"));
        Assert.False(RequestSigning.Verify("https://a/b.jpg", "zz", "k"));
    }
}
