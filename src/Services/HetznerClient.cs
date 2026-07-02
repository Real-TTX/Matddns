using System.Net.Http.Json;
using System.Text.Json;
using Matddns.Models;

namespace Matddns.Services;

/// <summary>Updates an A/AAAA/CNAME record via the Hetzner DNS API (Auth-API-Token): resolve zone id, find the record, update or create it.</summary>
public class HetznerClient
{
    private readonly IHttpClientFactory _http;
    private const string Api = "https://dns.hetzner.com/api/v1";

    public HetznerClient(IHttpClientFactory http) => _http = http;

    public async Task<(bool ok, string message)> UpdateRecordAsync(
        HetznerSettings cfg,
        string zone,
        string recordName,   // relative ("@" for apex, "home", "a.b")
        string recordType,
        string destination,
        bool allowCreate,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.ApiToken)) return (false, "API token missing");

        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Add("Auth-API-Token", cfg.ApiToken.Trim());

        var fqdn = recordName is "@" or "" ? zone : $"{recordName}.{zone}";

        // 1) zone name -> zone id
        using var zResp = await client.GetAsync($"{Api}/zones?name={Uri.EscapeDataString(zone)}", ct);
        var (zOk, zRoot, zErr) = await ReadAsync(zResp, ct);
        if (!zOk) return (false, zErr);
        string? zoneId = null;
        if (zRoot.TryGetProperty("zones", out var zones) && zones.ValueKind == JsonValueKind.Array && zones.GetArrayLength() > 0)
            zoneId = zones[0].GetProperty("id").GetString();
        if (zoneId == null) return (false, $"zone '{zone}' not found for this token");

        // 2) find the existing record (Hetzner stores the relative name; apex = "@")
        using var rResp = await client.GetAsync($"{Api}/records?zone_id={zoneId}", ct);
        var (rOk, rRoot, rErr) = await ReadAsync(rResp, ct);
        if (!rOk) return (false, rErr);

        string? recordId = null;
        string? currentValue = null;
        if (rRoot.TryGetProperty("records", out var records) && records.ValueKind == JsonValueKind.Array)
            foreach (var rec in records.EnumerateArray())
            {
                var name = rec.TryGetProperty("name", out var n) ? n.GetString() : null;
                var type = rec.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (string.Equals(name, recordName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(type, recordType, StringComparison.OrdinalIgnoreCase))
                {
                    recordId = rec.GetProperty("id").GetString();
                    currentValue = rec.TryGetProperty("value", out var v) ? v.GetString() : null;
                    break;
                }
            }

        if (recordId == null && !allowCreate) return (false, "record does not exist");
        if (recordId != null && string.Equals(currentValue, destination, StringComparison.OrdinalIgnoreCase))
            return (true, $"unchanged ({fqdn} {recordType} {destination})");

        // 3) update (PUT) or create (POST)
        var body = new { zone_id = zoneId, type = recordType, name = recordName, value = destination, ttl = 60 };
        HttpResponseMessage uResp = recordId != null
            ? await client.PutAsJsonAsync($"{Api}/records/{recordId}", body, ct)
            : await client.PostAsJsonAsync($"{Api}/records", body, ct);

        var (ok, _, err) = await ReadAsync(uResp, ct);
        return ok
            ? (true, $"{(recordId != null ? "updated" : "created")} {fqdn} {recordType} -> {destination}")
            : (false, err);
    }

    // Hetzner success = 2xx with a JSON object; errors come as {"error":{"message","code"}} or {"message"}
    private static async Task<(bool ok, JsonElement root, string error)> ReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        JsonElement root = default;
        var parsed = false;
        try
        {
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            root = doc.RootElement.Clone();
            parsed = root.ValueKind == JsonValueKind.Object;
        }
        catch { /* empty / non-JSON body */ }

        if (resp.IsSuccessStatusCode)
            return parsed ? (true, root, "") : (false, root, $"HTTP {(int)resp.StatusCode} — unexpected response (check the API token)");

        var msg = $"HTTP {(int)resp.StatusCode}";
        if (parsed)
        {
            if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var em))
                msg = em.GetString() ?? msg;
            else if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                msg = m.GetString() ?? msg;
        }
        return (false, root, msg);
    }
}
