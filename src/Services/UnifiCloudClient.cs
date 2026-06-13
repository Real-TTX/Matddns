using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Matddns.Services;

/// <summary>Reads gateways and their WAN IPs from the UniFi Site Manager cloud API (api.ui.com). One key sees every console on the account.</summary>
public class UnifiCloudClient
{
    public record HostInfo(string Id, string Name, string? Ipv4, string? Ipv6);

    private const string HostsUrl = "https://api.ui.com/v1/hosts";

    public async Task<List<HostInfo>> ListHostsAsync(string apiKey, CancellationToken ct)
    {
        using var doc = await GetAsync(apiKey, ct);
        var hosts = new List<HostInfo>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var h in data.EnumerateArray())
            {
                var id = TryGetString(h, "id");
                if (string.IsNullOrEmpty(id)) continue;
                var rs = h.TryGetProperty("reportedState", out var r) && r.ValueKind == JsonValueKind.Object ? r : default;
                var name = (rs.ValueKind == JsonValueKind.Object ? (TryGetString(rs, "name") ?? TryGetString(rs, "hostname")) : null) ?? id;

                // candidates, in preference order: the active uplink IP first, then each WAN.
                var v4 = new List<string?> { TryGetString(h, "ipAddress") };
                var v6 = new List<string?> { TryGetString(h, "ipAddress") };
                if (rs.ValueKind == JsonValueKind.Object && rs.TryGetProperty("wans", out var wans) && wans.ValueKind == JsonValueKind.Array)
                    foreach (var w in wans.EnumerateArray())
                    {
                        v4.Add(TryGetString(w, "ipv4"));
                        v6.Add(TryGetString(w, "ipv6"));
                    }

                hosts.Add(new HostInfo(id, name,
                    v4.FirstOrDefault(IsPublicV4),
                    v6.FirstOrDefault(IsGlobalV6)));
            }
        return hosts.OrderBy(h => h.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<JsonDocument> GetAsync(string apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("API key missing — create one in the UniFi Site Manager (unifi.ui.com).");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var req = new HttpRequestMessage(HttpMethod.Get, HostsUrl);
        req.Headers.TryAddWithoutValidation("X-API-KEY", apiKey);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var resp = await http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidOperationException($"{(int)resp.StatusCode} — key rejected. Use a Site Manager key (unifi.ui.com → API), not a local console key.");
        resp.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    private static bool IsPublicV4(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || s == "0.0.0.0") return false;
        if (!IPAddress.TryParse(s, out var a) || a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        if (b[0] == 10) return false;                          // 10.0.0.0/8
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false; // 172.16.0.0/12
        if (b[0] == 192 && b[1] == 168) return false;          // 192.168.0.0/16
        if (b[0] == 127) return false;                         // loopback
        if (b[0] == 169 && b[1] == 254) return false;          // link-local
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false; // CGNAT 100.64.0.0/10
        return true;
    }

    private static bool IsGlobalV6(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (!IPAddress.TryParse(s, out var a) || a.AddressFamily != AddressFamily.InterNetworkV6) return false;
        if (a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast || IPAddress.IsLoopback(a)) return false;
        return (a.GetAddressBytes()[0] & 0xfe) != 0xfc; // exclude ULA fc00::/7
    }

    private static string? TryGetString(JsonElement el, string name)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
        return null;
    }
}
