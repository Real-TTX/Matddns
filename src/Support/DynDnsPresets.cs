namespace Matddns.Support;

/// <summary>The update URLs of the DynDNS providers the form of a domain group can fill in. The page puts each URL on its option (<c>data-url</c>).</summary>
public static class DynDnsPresets
{
    public sealed record Preset(string Key, string Label, string Url);

    /// <param name="baseUrl">The address of this instance, for the preset that lets another Matddns feed it.</param>
    public static IReadOnlyList<Preset> All(string baseUrl) =>
    [
        new("matddns", "Matddns (another instance)", $"{baseUrl}/api/update?token={{password}}&ipv4={{ipv4}}&ipv6={{ipv6}}"),
        new("changeip", "ChangeIP", "https://nic.changeip.com/nic/update?hostname={hostname}&myip={ipv4}"),
        new("ddnss", "DDNSS.de", "https://www.ddnss.de/upd.php?key={password}&host={hostname}&ip={ipv4}&ip6={ipv6}"),
        new("desec", "deSEC", "https://update.dedyn.io/?hostname={hostname}&myipv4={ipv4}&myipv6={ipv6}"),
        new("dd24", "Domain Discount 24", "https://dynamicdns.key-systems.net/update.php?hostname={hostname}&password={password}&ip={ipv4}"),
        new("duckdns", "DuckDNS", "https://www.duckdns.org/update?domains={hostname}&token={password}&ip={ipv4}&ipv6={ipv6}"),
        new("dyndns", "DynDNS.org", "https://members.dyndns.org/v3/update?hostname={hostname}&myip={ipv4}&myipv6={ipv6}"),
        new("dnsomatic", "DNS-O-Matic", "https://updates.dnsomatic.com/nic/update?hostname={hostname}&myip={ipv4}"),
        new("dynu", "Dynu", "https://api.dynu.com/nic/update?hostname={hostname}&myip={ipv4}&myipv6={ipv6}"),
        new("dynv6", "DynV6", "https://dynv6.com/api/update?hostname={hostname}&ipv4={ipv4}&ipv6={ipv6}&token={password}"),
        new("easydns", "EasyDNS", "https://api.cp.easydns.com/dyn/tomato.php?hostname={hostname}&myip={ipv4}"),
        new("freedns", "FreeDNS (afraid.org)", "https://freedns.afraid.org/dynamic/update.php?{password}"),
        new("goip", "GoIP.de", "https://www.goip.de/setip?subdomain={hostname}&username={user}&password={password}&ip={ipv4}"),
        new("henet", "Hurricane Electric", "https://dyn.dns.he.net/nic/update?hostname={hostname}&myip={ipv4}"),
        new("ipv64", "ipv64.net", "https://ipv64.net/update.php?key={password}&domain={hostname}&ip={ipv4}&ip6={ipv6}"),
        new("noip", "No-IP", "https://dynupdate.no-ip.com/nic/update?hostname={hostname}&myip={ipv4}&myipv6={ipv6}"),
        new("nowdns", "Now-DNS", "https://now-dns.com/update?hostname={hostname}&myip={ipv4}"),
        new("opendns", "OpenDNS", "https://updates.opendns.com/nic/update?hostname={hostname}&myip={ipv4}"),
        new("selfhost", "Selfhost.de", "https://carol.selfhost.de/nic/update?hostname={hostname}&myip={ipv4}"),
        new("spdyn", "SPDYN", "https://update.spdyn.de/nic/update?hostname={hostname}&myip={ipv4}&myipv6={ipv6}"),
        new("strato", "Strato", "https://dyndns.strato.com/nic/update?hostname={hostname}&myip={ipv4}&myipv6={ipv6}"),
        new("variomedia", "Variomedia", "https://dyndns.variomedia.de/nic/update?hostname={hostname}&myip={ipv4}&myipv6={ipv6}"),
        new("zoneedit", "Zoneedit", "https://dynamic.zoneedit.com/auth/dynamic.html?host={hostname}&dnsto={ipv4}"),
    ];
}
