using System.Net;
using System.Net.Sockets;

namespace Plinth.Pipeline.Fetch;

/// <summary>Addresses a public image fetcher must never connect to.</summary>
public static class PrivateAddress
{
    public static bool IsBlocked(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetwork) return IsBlockedV4(ip.GetAddressBytes(), 0);
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IPv6Loopback.Equals(ip) || IPAddress.IPv6Any.Equals(ip)) return true;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xfe) == 0xfc) return true;                     // fc00::/7 unique local
            if (b[0] == 0xfe && (b[1] & 0xc0) == 0x80) return true;      // fe80::/10 link-local
            if (b[0] == 0xff) return true;                               // multicast

            // Teredo (2001:0000::/32) tunnels to whatever IPv4 peer the address encodes, with
            // the interesting half obfuscated; nothing worth fetching is ever behind one.
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) return true;

            // The transition formats carry an IPv4 address inside an IPv6 one. Judging them by
            // the IPv6 rules alone would hand back every IPv4 rule above: ::10.0.0.1,
            // 64:ff9b::a00:1 and 2002:0a00:0001:: all reach 10.0.0.1. Judge what they carry.
            if (IsIPv4Compatible(b) || IsNat64(b)) return IsBlockedV4(b, 12);
            if (b[0] == 0x20 && b[1] == 0x02) return IsBlockedV4(b, 2);  // 6to4 2002::/16

            return false;
        }
        return true;
    }

    private static bool IsBlockedV4(byte[] b, int o) =>
        b[o] == 0                                                  // 0.0.0.0/8
        || b[o] == 10                                              // 10/8
        || b[o] == 127                                             // loopback
        || (b[o] == 100 && b[o + 1] >= 64 && b[o + 1] <= 127)      // 100.64/10 CGNAT
        || (b[o] == 169 && b[o + 1] == 254)                        // link-local incl. 169.254.169.254
        || (b[o] == 172 && b[o + 1] >= 16 && b[o + 1] <= 31)       // 172.16/12
        || (b[o] == 192 && b[o + 1] == 168)                        // 192.168/16
        || b[o] >= 224;                                            // multicast + reserved + broadcast

    /// <summary>::a.b.c.d — the first 96 bits zero, with a non-zero address in the last 32.</summary>
    private static bool IsIPv4Compatible(byte[] b)
    {
        for (var i = 0; i < 12; i++) if (b[i] != 0) return false;
        return b[12] != 0 || b[13] != 0 || b[14] != 0 || b[15] != 0;
    }

    /// <summary>64:ff9b::/96, the well-known NAT64 prefix.</summary>
    private static bool IsNat64(byte[] b)
    {
        if (b[0] != 0x00 || b[1] != 0x64 || b[2] != 0xff || b[3] != 0x9b) return false;
        for (var i = 4; i < 12; i++) if (b[i] != 0) return false;
        return true;
    }
}
