using Matddns.Models;

namespace Matddns.Services;

/// <summary>Writes one value to one record at its provider; shared by the updater (rules) and the dynamic receiver (pushed hostnames).</summary>
public class RecordWriter
{
    private readonly DynDnsClient _dyndns;
    private readonly NetcupClient _netcup;
    private readonly CloudflareClient _cloudflare;
    private readonly HetznerClient _hetzner;
    private readonly GoDaddyClient _godaddy;
    private readonly MatddnsClient _matddns;

    public RecordWriter(DynDnsClient dyndns, NetcupClient netcup, CloudflareClient cloudflare, HetznerClient hetzner, GoDaddyClient godaddy, MatddnsClient matddns)
    {
        _dyndns = dyndns;
        _netcup = netcup;
        _cloudflare = cloudflare;
        _hetzner = hetzner;
        _godaddy = godaddy;
        _matddns = matddns;
    }

    /// <summary>Whether a dynamic receiver can create records in a group of <paramref name="kind"/>: the zone-based APIs.</summary>
    public static bool CanCreate(DomainKind kind) => kind is DomainKind.Netcup or DomainKind.Cloudflare or DomainKind.Hetzner or DomainKind.GoDaddy;

    /// <summary>Whether the group lets Matddns create records that do not exist yet (its "allow dynamic records" switch).</summary>
    public static bool AllowsCreate(DomainGroup g) => g.Kind switch
    {
        DomainKind.Netcup => g.Netcup?.AllowDynamic == true,
        DomainKind.Cloudflare => g.Cloudflare?.AllowDynamic == true,
        DomainKind.Hetzner => g.Hetzner?.AllowDynamic == true,
        DomainKind.GoDaddy => g.GoDaddy?.AllowDynamic == true,
        _ => false,
    };

    // Writes the value to the target provider and returns its result message.
    public async Task<(bool ok, string message)> WriteAsync(DomainGroup group, DomainEntry entry, string value, CancellationToken ct)
    {
        var recordType = entry.Type.ToString();
        switch (group.Kind)
        {
            case DomainKind.DynDns when group.DynDns is { } dynDns:
                return entry.Type == DnsRecordType.CNAME
                    ? (false, "CNAME not supported via DynDNS update URL")
                    : await _dyndns.UpdateAsync(dynDns, entry.Hostname, value, ct);

            case DomainKind.Netcup when group.Netcup is { } netcup:
            {
                var (zone, host) = ZoneAndHost(entry);
                return await _netcup.UpdateRecordAsync(netcup, zone, host, recordType, value, netcup.AllowDynamic, ct);
            }

            case DomainKind.Cloudflare when group.Cloudflare is { } cloudflare:
            {
                var zone = !string.IsNullOrWhiteSpace(entry.Domain) ? entry.Domain! : NetcupClient.SplitHostname(entry.Hostname).Domain;
                return await _cloudflare.UpdateRecordAsync(cloudflare, zone, entry.Hostname, recordType, value, cloudflare.AllowDynamic, ct);
            }

            case DomainKind.Hetzner when group.Hetzner is { } hetzner:
            {
                var (zone, host) = ZoneAndHost(entry);
                return await _hetzner.UpdateRecordAsync(hetzner, zone, host, recordType, value, hetzner.AllowDynamic, ct);
            }

            case DomainKind.GoDaddy when group.GoDaddy is { } godaddy:
            {
                var (zone, host) = ZoneAndHost(entry);
                return await _godaddy.UpdateRecordAsync(godaddy, zone, host, recordType, value, godaddy.AllowDynamic, ct);
            }

            case DomainKind.Matddns when group.Matddns is { } peer:
                if (entry.Type == DnsRecordType.CNAME) return (false, "CNAME not supported via Matddns push");
                return await _matddns.PushAsync(peer, entry.Hostname,
                    entry.Type == DnsRecordType.A ? value : null,
                    entry.Type == DnsRecordType.AAAA ? value : null, ct);

            default:
                return (false, "no credentials configured");
        }
    }

    // Zone + relative record name for zone-based APIs. Prefers the explicit Domain/RecordName, else derives from the FQDN.
    private static (string zone, string host) ZoneAndHost(DomainEntry de)
    {
        if (!string.IsNullOrWhiteSpace(de.Domain))
            return (de.Domain!, string.IsNullOrWhiteSpace(de.RecordName) ? "@" : de.RecordName!);
        var (domain, host) = NetcupClient.SplitHostname(de.Hostname);
        if (!string.IsNullOrWhiteSpace(de.RecordName)) host = de.RecordName!;
        return (domain, host);
    }
}
