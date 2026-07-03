using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Matddns.Models;

namespace Matddns.Services;

/// <summary>Updates an A/AAAA/CNAME record via the Cloudflare API v4 (Bearer API Token): resolve zone id, find the record, update or create it.</summary>
public class CloudflareClient
{
    private readonly IHttpClientFactory _http;
    private const string Api = "https://api.cloudflare.com/client/v4";

    public CloudflareClient(IHttpClientFactory http) => _http = http;

    public async Task<(bool ok, string message)> UpdateRecordAsync(
        CloudflareSettings cfg,
        string zone,
        string fqdn,
        string recordType,
        string destination,
        bool allowCreate,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.ApiToken)) return (false, "API token missing");

        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiToken.Trim());

        // 1) zone name -> zone id
        var (zoneId, zErr) = await GetZoneIdAsync(client, zone, ct);
        if (zoneId == null) return (false, zErr ?? $"zone '{zone}' not found");

        // 2) find the existing record (by name + type)
        using var listResp = await client.GetAsync($"{Api}/zones/{zoneId}/dns_records?type={recordType}&name={Uri.EscapeDataString(fqdn)}", ct);
        var (listOk, listRoot, listErr) = await ReadAsync(listResp, ct);
        if (!listOk) return (false, listErr);

        string? recordId = null;
        string? currentContent = null;
        bool currentProxied = false;
        if (listRoot.TryGetProperty("result", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
        {
            var rec = arr[0];
            recordId = rec.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            currentContent = rec.TryGetProperty("content", out var c) ? c.GetString() : null;
            currentProxied = rec.TryGetProperty("proxied", out var p) && p.ValueKind == JsonValueKind.True;
        }

        if (recordId == null && !allowCreate)
            return (false, "record does not exist");

        if (recordId != null && string.Equals(currentContent, destination, StringComparison.OrdinalIgnoreCase))
            return (true, $"unchanged ({fqdn} {recordType} {destination})");

        // 3) update existing (PUT) or create (POST); keep the record's proxied flag, ttl=1 (=auto)
        var body = new { type = recordType, name = fqdn, content = destination, ttl = 1, proxied = currentProxied };
        HttpResponseMessage resp = recordId != null
            ? await client.PutAsJsonAsync($"{Api}/zones/{zoneId}/dns_records/{recordId}", body, ct)
            : await client.PostAsJsonAsync($"{Api}/zones/{zoneId}/dns_records", body, ct);

        var (ok, _, err) = await ReadAsync(resp, ct);
        return ok
            ? (true, $"{(recordId != null ? "updated" : "created")} {fqdn} {recordType} -> {destination}")
            : (false, err);
    }

    private async Task<(string? id, string? error)> GetZoneIdAsync(HttpClient client, string zone, CancellationToken ct)
    {
        // no &status=active: Cloudflare already scopes /zones to token-visible zones, and a freshly added
        // zone that is still 'pending' (delegation propagating) is editable but would be filtered out.
        using var resp = await client.GetAsync($"{Api}/zones?name={Uri.EscapeDataString(zone)}", ct);
        var (ok, root, err) = await ReadAsync(resp, ct);
        if (!ok) return (null, err);
        if (root.TryGetProperty("result", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0
            && arr[0].TryGetProperty("id", out var idEl))
            return (idEl.GetString(), null);
        return (null, $"zone '{zone}' not found for this token");
    }

    // Cloudflare envelope: { success, errors:[{code,message}], result }
    private static async Task<(bool ok, JsonElement root, string error)> ReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        JsonDocument doc;
        try { doc = await JsonDocumentFromAsync(resp, ct); }
        catch { return (false, default, $"HTTP {(int)resp.StatusCode}"); }

        var root = doc.RootElement.Clone();
        doc.Dispose();
        var success = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
        if (success) return (true, root, "");

        var msg = $"HTTP {(int)resp.StatusCode}";
        if (root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0)
        {
            var e0 = errs[0];
            var m = e0.TryGetProperty("message", out var mm) ? mm.GetString() : null;
            var code = e0.TryGetProperty("code", out var cc) ? cc.ToString() : null;
            if (!string.IsNullOrEmpty(m)) msg = code != null ? $"{m} ({code})" : m;
        }
        return (false, root, msg);
    }

    private static async Task<JsonDocument> JsonDocumentFromAsync(HttpResponseMessage resp, CancellationToken ct) =>
        await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
}
