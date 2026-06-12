using System.Net;
using System.Net.Sockets;

namespace Matddns.Services;

/// <summary>Resolves a hostname to its current A / AAAA address (e.g. a MyFRITZ! or other DynDNS name).</summary>
public class DnsLookupClient
{
    public async Task<(string? Ipv4, string? Ipv6)> ResolveAsync(string? host, CancellationToken ct)
    {
        host = (host ?? "").Trim();
        if (string.IsNullOrEmpty(host)) return (null, null);

        // Query each family explicitly so a v4-only host can still read the target's AAAA
        // (AF_UNSPEC drops AAAA when the local box has no IPv6 source address).
        var v4 = await FirstAsync(host, AddressFamily.InterNetwork, ct);
        var v6 = await FirstAsync(host, AddressFamily.InterNetworkV6, ct);
        return (v4, v6);
    }

    private static async Task<string?> FirstAsync(string host, AddressFamily family, CancellationToken ct)
    {
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host, family, ct);
            return addrs.FirstOrDefault(a => a.AddressFamily == family
                                             && !a.IsIPv6LinkLocal && !a.IsIPv6SiteLocal)?.ToString();
        }
        catch { return null; }
    }
}
