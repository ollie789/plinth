using System.Net;
using System.Net.Sockets;

namespace Plinth.Pipeline.Fetch;

/// <summary>Addresses a public image fetcher must never connect to.</summary>
public static class PrivateAddress
{
    public static bool IsBlocked(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 0                                   // 0.0.0.0/8
                || b[0] == 10                                  // 10/8
                || b[0] == 127                                 // loopback
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)   // 100.64/10 CGNAT
                || (b[0] == 169 && b[1] == 254)                 // link-local incl. 169.254.169.254
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)    // 172.16/12
                || (b[0] == 192 && b[1] == 168)                 // 192.168/16
                || b[0] >= 224;                                // multicast + reserved + broadcast
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IPv6Loopback.Equals(ip) || IPAddress.IPv6Any.Equals(ip)) return true;
            var b = ip.GetAddressBytes();
            return (b[0] & 0xfe) == 0xfc                      // fc00::/7 unique local
                || (b[0] == 0xfe && (b[1] & 0xc0) == 0x80)     // fe80::/10 link-local
                || b[0] == 0xff;                               // multicast
        }
        return true;
    }
}
