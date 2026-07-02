namespace Matddns.Models;

public enum SourceKind
{
    PublicIp,
    Unifi,
    Static,
    Push,
    Dns,
    Fritzbox,
    UnifiCloud,
    Matddns
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
    public FritzboxSettings? Fritzbox { get; set; }
    public UnifiCloudSettings? UnifiCloud { get; set; }
    public MatddnsLinkSettings? Matddns { get; set; }
    public List<SourceEntry> Entries { get; set; } = new();
}

/// <summary>Link to another Matddns instance (its base URL + a DynDNS-Server token on that instance). Used by the Matddns source (pull) and the Matddns target (push).</summary>
public class MatddnsLinkSettings
{
    public string BaseUrl { get; set; } = "";   // e.g. https://dyndns.example.com
    public string Token { get; set; } = "";     // a DynDNS-Server source token on the remote instance
}

public class UnifiCloudSettings
{
    public string ApiKey { get; set; } = "";   // UniFi Site Manager key (X-API-KEY), api.ui.com
    // selected gateways are stored as SourceEntry rows (InterfaceName = host id, Label = gateway name)
}

public class FritzboxSettings
{
    public string BaseUrl { get; set; } = "";   // local: http://fritz.box:49000  ·  MyFRITZ: https://<id>.myfritz.net:<port>
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool IgnoreCertificate { get; set; }
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
