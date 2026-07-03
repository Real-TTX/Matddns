using System.Net;
using System.Net.Sockets;

namespace Matddns.Services;

/// <summary>Shared address checks that keep private/reserved IPs out of DNS records.</summary>
public static class IpFilter
{
    /// <summary>True only for a globally routable IPv4 (rejects 0.0.0.0, RFC1918, loopback, link-local, CGNAT).</summary>
    public static bool IsPublicV4(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || s == "0.0.0.0") return false;
        if (!IPAddress.TryParse(s, out var a) || a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        if (b[0] == 10) return false;                               // 10.0.0.0/8
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;  // 172.16.0.0/12
        if (b[0] == 192 && b[1] == 168) return false;               // 192.168.0.0/16
        if (b[0] == 127) return false;                              // loopback
        if (b[0] == 169 && b[1] == 254) return false;               // link-local
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false; // CGNAT 100.64.0.0/10
        return true;
    }

    /// <summary>True only for a globally routable IPv6 (rejects link-local, ULA fc00::/7, site-local, multicast, loopback).</summary>
    public static bool IsGlobalV6(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (!IPAddress.TryParse(s, out var a) || a.AddressFamily != AddressFamily.InterNetworkV6) return false;
        if (a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast || IPAddress.IsLoopback(a)) return false;
        return (a.GetAddressBytes()[0] & 0xfe) != 0xfc; // exclude ULA fc00::/7
    }
}
