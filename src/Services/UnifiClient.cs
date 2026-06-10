using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Matddns.Models;

namespace Matddns.Services;

public class UnifiClient
{
    public record WanInfo(string Name, string? Ip, bool Up, string? GatewayMac, string? Ifname = null, string? DisplayName = null);

    public async Task<List<WanInfo>> GetWansAsync(UnifiSettings cfg, CancellationToken ct)
    {
        using var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            ServerCertificateCustomValidationCallback = cfg.IgnoreCertificate ? (_, _, _, _) => true : null
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        http.BaseAddress = NormalizeBaseUrl(cfg.BaseUrl);

        var isUnifiOs = await LoginAsync(http, cfg, ct);
        var pathPrefix = isUnifiOs ? "/proxy/network" : "";
        var site = string.IsNullOrWhiteSpace(cfg.Site) ? "default" : cfg.Site;

        // 1) Gateway-Device: ifname->networkgroup (ethernet_overrides) + Live-IP/Status je WAN-Port.
        var ifnameToGroup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var liveByGroup = new Dictionary<string, (string? Ip, bool Up, string? Ifname)>(StringComparer.OrdinalIgnoreCase);
        string? gwMac = null;

        using (var devResp = await http.GetAsync($"{pathPrefix}/api/s/{site}/stat/device", ct))
        {
            devResp.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await devResp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var dev in data.EnumerateArray())
                {
                    if (TryGetString(dev, "type") is not ("ugw" or "udm" or "uxg")) continue;
                    gwMac = TryGetString(dev, "mac");

                    if (dev.TryGetProperty("ethernet_overrides", out var eo) && eo.ValueKind == JsonValueKind.Array)
                        foreach (var e in eo.EnumerateArray())
                        {
                            var ifn = TryGetString(e, "ifname");
                            var grp = TryGetString(e, "networkgroup");
                            if (!string.IsNullOrEmpty(ifn) && !string.IsNullOrEmpty(grp))
                                ifnameToGroup[ifn] = grp;
                        }

                    for (int i = 1; i <= 8; i++)
                    {
                        if (!dev.TryGetProperty($"wan{i}", out var w) || w.ValueKind != JsonValueKind.Object) continue;
                        var ifname = TryGetString(w, "ifname") ?? TryGetString(w, "name");
                        var ip = TryGetString(w, "ip");
                        var up = TryGetBool(w, "up") ?? !string.IsNullOrEmpty(ip);
                        var group = ifname != null && ifnameToGroup.TryGetValue(ifname, out var g)
                            ? g
                            : (i == 1 ? "WAN" : $"WAN{i}");
                        liveByGroup[group] = (string.IsNullOrEmpty(ip) ? null : ip, up, ifname);
                    }
                }
            }
        }

        // 2) networkconf: all configured WANs with display name + group (incl. LTE failover without a port).
        var result = new List<WanInfo>();
        try
        {
            using var ncResp = await http.GetAsync($"{pathPrefix}/api/s/{site}/rest/networkconf", ct);
            if (ncResp.IsSuccessStatusCode)
            {
                using var doc = await JsonDocument.ParseAsync(await ncResp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var n in data.EnumerateArray())
                    {
                        if ((TryGetString(n, "purpose") ?? "") != "wan") continue;
                        var group = TryGetString(n, "wan_networkgroup") ?? "WAN";
                        var display = TryGetString(n, "name");
                        var cfgIp = TryGetString(n, "wan_ip");
                        liveByGroup.TryGetValue(group, out var live);
                        var ip = !string.IsNullOrEmpty(live.Ip) ? live.Ip : (string.IsNullOrEmpty(cfgIp) ? null : cfgIp);
                        var up = live.Ip != null ? live.Up : !string.IsNullOrEmpty(cfgIp);
                        result.Add(new WanInfo(group, ip, up, gwMac, live.Ifname, display));
                    }
                }
            }
        }
        catch { /* networkconf optional */ }

        // 3) Fallback without networkconf: directly from the gateway's live groups.
        if (result.Count == 0)
            foreach (var kv in liveByGroup)
                result.Add(new WanInfo(kv.Key, kv.Value.Ip, kv.Value.Up, gwMac, kv.Value.Ifname, null));

        // order: WAN, WAN2, … and LTE failover last.
        return result.OrderBy(GroupRank).ToList();
    }

    private static int GroupRank(WanInfo w)
    {
        var n = w.Name.ToUpperInvariant();
        if (n.Contains("LTE") || n.Contains("FAILOVER")) return 900;
        var m = System.Text.RegularExpressions.Regex.Match(n, @"WAN(\d*)");
        if (m.Success) return m.Groups[1].Value == "" ? 1 : int.Parse(m.Groups[1].Value);
        return 500;
    }

    public static Uri NormalizeBaseUrl(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (string.IsNullOrEmpty(s))
            throw new InvalidOperationException("Base URL is empty — please enter e.g. https://10.10.0.1.");
        if (!s.Contains("://", StringComparison.Ordinal)) s = "https://" + s;
        s = s.TrimEnd('/');
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new InvalidOperationException($"Base URL invalid: '{raw}'. Expected e.g. https://10.10.0.1");
        return uri;
    }

    private static async Task<bool> LoginAsync(HttpClient http, UnifiSettings cfg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.Username) || string.IsNullOrWhiteSpace(cfg.Password))
            throw new InvalidOperationException("Username/password missing for the Unifi login.");

        var body = new { username = cfg.Username, password = cfg.Password, remember = true };

        // 1) UniFi OS (UDM / UDR / Cloud Key Gen2+)
        System.Net.HttpStatusCode? osStatus = null;
        string osBody = "";
        try
        {
            using var resp = await http.PostAsJsonAsync("/api/auth/login", body, ct);
            osStatus = resp.StatusCode;
            if (resp.IsSuccessStatusCode)
            {
                if (resp.Headers.TryGetValues("X-CSRF-Token", out var tokens))
                {
                    var token = tokens.FirstOrDefault();
                    if (!string.IsNullOrEmpty(token))
                        http.DefaultRequestHeaders.Add("X-CSRF-Token", token);
                }
                return true;
            }
            osBody = await SafeRead(resp, ct);
        }
        catch (Exception ex) { osBody = ex.Message; }

        // 2) Legacy controller (software / older hardware)
        System.Net.HttpStatusCode? legacyStatus = null;
        string legacyBody = "";
        try
        {
            using var legacy = await http.PostAsJsonAsync("/api/login", body, ct);
            legacyStatus = legacy.StatusCode;
            if (legacy.IsSuccessStatusCode) return false;
            legacyBody = await SafeRead(legacy, ct);
        }
        catch (Exception ex) { legacyBody = ex.Message; }

        var hint = (osStatus == System.Net.HttpStatusCode.Unauthorized || legacyStatus == System.Net.HttpStatusCode.Unauthorized
                    || osStatus == System.Net.HttpStatusCode.BadRequest)
            ? " — Tip: use a local UniFi account (no Ubiquiti cloud/SSO login) and disable 2FA for this account."
            : "";

        throw new InvalidOperationException(
            $"Login failed. UniFi-OS /api/auth/login: {Describe(osStatus, osBody)}; Legacy /api/login: {Describe(legacyStatus, legacyBody)}.{hint}");
    }

    private static string Describe(System.Net.HttpStatusCode? status, string body)
    {
        var s = status.HasValue ? $"{(int)status.Value} {status.Value}" : "no response";
        body = (body ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (body.Length > 120) body = body[..120] + "…";
        return string.IsNullOrEmpty(body) ? s : $"{s} ({body})";
    }

    private static async Task<string> SafeRead(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); } catch { return ""; }
    }

    private static string? TryGetString(JsonElement el, string name)
    {
        if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
        return null;
    }

    private static bool? TryGetBool(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}
