namespace Matddns.Models;

public enum DomainKind
{
    DynDns,
    Netcup,
    Cloudflare,
    Hetzner,
    GoDaddy,
    Matddns
}

public enum DnsRecordType
{
    A,
    AAAA,
    CNAME
}

public class DomainGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public DomainKind Kind { get; set; } = DomainKind.DynDns;
    public DynDnsSettings? DynDns { get; set; }
    public NetcupSettings? Netcup { get; set; }
    public CloudflareSettings? Cloudflare { get; set; }
    public HetznerSettings? Hetzner { get; set; }
    public GoDaddySettings? GoDaddy { get; set; }
    public MatddnsLinkSettings? Matddns { get; set; }   // push to another Matddns instance (its JSON API)
    public List<DomainEntry> Entries { get; set; } = new();

    /// <summary>Zone-based API providers use a record-name + zone (like Netcup); DynDNS just uses the full FQDN.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsZoneBased => Kind is DomainKind.Netcup or DomainKind.Cloudflare or DomainKind.Hetzner or DomainKind.GoDaddy;
}

public class CloudflareSettings
{
    public string ApiToken { get; set; } = "";   // Cloudflare API Token with Zone.DNS edit (Bearer)

    /// <summary>Opt-in: create records that don't exist yet (needed for dynamic hosts). Off = update existing only.</summary>
    public bool AllowDynamic { get; set; }
}

public class HetznerSettings
{
    public string ApiToken { get; set; } = "";   // Hetzner DNS API token (Auth-API-Token header)
    public bool AllowDynamic { get; set; }
}

public class GoDaddySettings
{
    public string ApiKey { get; set; } = "";     // GoDaddy API key + secret ("Authorization: sso-key key:secret")
    public string ApiSecret { get; set; } = "";
    public bool AllowDynamic { get; set; }
}

public class DynDnsSettings
{
    public string UpdateUrl { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public class NetcupSettings
{
    public string CustomerNumber { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ApiPassword { get; set; } = "";

    /// <summary>Opt-in: allow Matddns to create records that don't exist yet (needed for dynamic hosts). Off = update existing only.</summary>
    public bool AllowDynamic { get; set; }
}

public class DomainEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Hostname { get; set; } = "";          // voller FQDN, z.B. home1.h5x.de
    public DnsRecordType Type { get; set; } = DnsRecordType.A;
    public string? RecordName { get; set; }              // Netcup: Host/Record-Teil, z.B. @, www, home1
    public string? Domain { get; set; }                  // Netcup: Zone, z.B. h5x.de

    /// <summary>Voller Anzeigename inkl. Typ, z.B. "home1.h5x.de (A)".</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Display => $"{Hostname} ({Type})";
}
