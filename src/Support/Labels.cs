using Matddns.Models;

namespace Matddns.Support;

/// <summary>The names the pages show for the kinds of sources and domain groups, and the state of a source group.</summary>
public static class Labels
{
    public static string Source(SourceKind kind) => kind switch
    {
        SourceKind.Unifi => "Unifi",
        SourceKind.UnifiCloud => "UniFi Cloud",
        SourceKind.Static => "Static IP",
        SourceKind.Push => "DynDNS Server",
        SourceKind.Dns => "DNS lookup",
        SourceKind.Fritzbox => "FRITZ!Box",
        SourceKind.Matddns => "Matddns peer",
        _ => "Public IP"
    };

    public static string Domain(DomainKind kind) => kind switch
    {
        DomainKind.Netcup => "Netcup API",
        DomainKind.Cloudflare => "Cloudflare",
        DomainKind.Hetzner => "Hetzner DNS",
        DomainKind.GoDaddy => "GoDaddy",
        DomainKind.Matddns => "Matddns peer",
        _ => "DynDNS"
    };

    /// <summary>"err" when an entry reports an error, "ok" when an entry has an address, otherwise "warn" (nothing yet). The value of <see cref="Ui.Pill"/>.</summary>
    public static string State(SourceGroup group)
        => group.Entries.Any(e => e.LastError != null) ? "err"
            : group.Entries.Any(e => !string.IsNullOrEmpty(e.CurrentIp) || !string.IsNullOrEmpty(e.CurrentIpv6)) ? "ok"
            : "warn";

    public static string StateText(string state) => state switch { "err" => "Error", "ok" => "OK", _ => "Pending" };
}
