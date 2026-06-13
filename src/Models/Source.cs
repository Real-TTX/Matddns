namespace Matddns.Models;

public enum SourceKind
{
    PublicIp,
    Unifi,
    Static,
    Push,
    Dns
}

public class SourceGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public SourceKind Kind { get; set; } = SourceKind.PublicIp;
    public int IntervalSeconds { get; set; } = 60;
    public UnifiSettings? Unifi { get; set; }
    public StaticSettings? Static { get; set; }
    public PushSettings? Push { get; set; }
    public DnsSettings? Dns { get; set; }
    public List<SourceEntry> Entries { get; set; } = new();
}

public class DnsSettings
{
    public string Hostname { get; set; } = "";   // resolved to A/AAAA, e.g. xxxxxxxx.myfritz.net
}

public class StaticSettings
{
    public string Ip { get; set; } = "";     // IPv4 (optional)
    public string Ipv6 { get; set; } = "";   // IPv6 (optional) – either or both
}

public class PushSettings
{
    public string Token { get; set; } = "";   // secret in the update URL / dyndns2 basic-auth password

    // Dynamic (wildcard) receiver: pushes carry a hostname; records under the namespace are created/updated on demand.
    public bool Dynamic { get; set; }
    public string TargetDomainGroupId { get; set; } = ""; // a Netcup group with AllowDynamic
    public string Zone { get; set; } = "";                // the Netcup zone, e.g. h5x.de
    public string Prefix { get; set; } = "";              // sub-namespace under the zone, e.g. "dynamic" (empty = whole zone)

    /// <summary>Full base of the namespace, e.g. "dynamic.h5x.de" (or just the zone when no prefix).</summary>
    public string BaseFqdn => string.IsNullOrWhiteSpace(Prefix) ? Zone : $"{Prefix.Trim('.')}.{Zone}";
}

public class UnifiSettings
{
    public string BaseUrl { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Site { get; set; } = "default";
    public bool IgnoreCertificate { get; set; } = true;
}

public class SourceEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "";
    public string? InterfaceName { get; set; }
    public string? CurrentIp { get; set; }       // IPv4
    public string? CurrentIpv6 { get; set; }     // IPv6
    public DateTime? LastChecked { get; set; }
    public string? LastError { get; set; }
}
