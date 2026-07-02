using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Matddns.Models;

namespace Matddns.Services;

/// <summary>Updates an A/AAAA/CNAME record via the GoDaddy DNS API ("Authorization: sso-key key:secret"): GET to compare/exist-check, PUT to replace or create.</summary>
public class GoDaddyClient
{
    private readonly IHttpClientFactory _http;
    private const string Api = "https://api.godaddy.com/v1";

    public GoDaddyClient(IHttpClientFactory http) => _http = http;

    public async Task<(bool ok, string message)> UpdateRecordAsync(
        GoDaddySettings cfg,
        string zone,
        string recordName,   // relative ("@" for apex)
        string recordType,
        string destination,
        bool allowCreate,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.ApiKey) || string.IsNullOrWhiteSpace(cfg.ApiSecret))
            return (false, "API key/secret missing");

        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("sso-key", $"{cfg.ApiKey.Trim()}:{cfg.ApiSecret.Trim()}");

        var name = string.IsNullOrWhiteSpace(recordName) ? "@" : recordName;
        var fqdn = name == "@" ? zone : $"{name}.{zone}";
        var path = $"{Api}/domains/{Uri.EscapeDataString(zone)}/records/{recordType}/{Uri.EscapeDataString(name)}";

        // 1) read existing records of this type+name
        bool exists = false;
        string? currentData = null;
        using (var getResp = await client.GetAsync(path, ct))
        {
            if (!getResp.IsSuccessStatusCode)
                return (false, await ErrorAsync(getResp, ct));
            try
            {
                using var doc = await JsonDocument.ParseAsync(await getResp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                {
                    exists = true;
                    currentData = doc.RootElement[0].TryGetProperty("data", out var d) ? d.GetString() : null;
                }
            }
            catch { /* treat as not-found */ }
        }

        if (!exists && !allowCreate) return (false, "record does not exist");
        if (exists && string.Equals(currentData, destination, StringComparison.OrdinalIgnoreCase))
            return (true, $"unchanged ({fqdn} {recordType} {destination})");

        // 2) PUT replaces all records of this type+name (creates if none existed)
        var body = new[] { new { data = destination, ttl = 600 } };
        using var putResp = await client.PutAsJsonAsync(path, body, ct);
        if (putResp.IsSuccessStatusCode)
            return (true, $"{(exists ? "updated" : "created")} {fqdn} {recordType} -> {destination}");
        return (false, await ErrorAsync(putResp, ct));
    }

    // GoDaddy errors: {"code":"...","message":"..."}
    private static async Task<string> ErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
            {
                var code = doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
                return code != null ? $"{m.GetString()} ({code})" : m.GetString()!;
            }
        }
        catch { /* fall through */ }
        return $"HTTP {(int)resp.StatusCode}";
    }
}
