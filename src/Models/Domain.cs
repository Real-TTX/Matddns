namespace Matddns.Models;

public enum DomainKind
{
    DynDns,
    Netcup
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
    public List<DomainEntry> Entries { get; set; } = new();
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
