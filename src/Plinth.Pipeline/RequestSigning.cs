using System.Security.Cryptography;
using System.Text;

namespace Plinth.Pipeline;

/// <summary>Optional HMAC on the src parameter so a public endpoint cannot be used as a free normaliser.</summary>
public static class RequestSigning
{
    public static string Sign(string src, string key) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(src)));

    public static bool Verify(string src, string? sig, string key)
    {
        if (string.IsNullOrEmpty(sig)) return false;
        byte[] given;
        try { given = Convert.FromHexString(sig); } catch (FormatException) { return false; }
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(src));
        return CryptographicOperations.FixedTimeEquals(given, expected);
    }
}
