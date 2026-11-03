using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Matddns.Models;

namespace Matddns.Services;

/// <summary>Updates an A/AAAA/CNAME record via the new netcup CloudDNS REST API (api.netcup.com/v1): resolve the
/// numeric zone id, read the records of the active revision, then add (and replace) the record in one changeset.
/// Auth is a single long-lived Bearer key created in the CCP under Master Data → API → "API Keys" (not the legacy one).</summary>
public class NetcupCloudClient
{
    private readonly IHttpClientFactory _http;
    private const string Api = "https://api.netcup.com/v1";

    public NetcupCloudClient(IHttpClientFactory http) => _http = http;

    public async Task<(bool ok, string message)> UpdateRecordAsync(
        NetcupCloudSettings cfg,
        string zone,
        string recordName,   // relative ("@" for apex, "home", "a.b")
        string recordType,
        string destination,
        bool allowCreate,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.ApiKey)) return (false, "API key missing");

        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey.Trim());
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");

        var name = string.IsNullOrWhiteSpace(recordName) ? "@" : recordName;
        var fqdn = name is "@" or "*" ? zone : $"{name}.{zone}";

        // 1) zone name -> numeric domain id; it must be on the CloudDNS (new) backend
        var (zoneId, managed, zErr) = await FindDomainAsync(client, zone, ct);
        if (zErr != null) return (false, zErr);
        if (zoneId == null) return (false, $"zone '{zone}' not found for this API key");
        if (managed == false) return (false, $"zone '{zone}' is still on the legacy backend - use Netcup legacy");

        // 2) the active (published) revision holds the live records
        var (revId, revErr) = await ActiveRevisionAsync(client, zoneId.Value, ct);
        if (revId == null) return (false, revErr ?? "no active DNS revision");

        // 3) find the existing record (by name + type) in that revision
        var (recOk, oldRdata, oldTtl, recErr) = await FindRecordAsync(client, zoneId.Value, revId, name, recordType, ct);
        if (!recOk) return (false, recErr!);

        if (oldRdata != null && string.Equals(oldRdata, destination, StringComparison.OrdinalIgnoreCase))
            return (true, $"unchanged ({fqdn} {recordType} {destination})");
        if (oldRdata == null && !allowCreate)
            return (false, "record does not exist");

        // 4) one changeset: create the new record and (if present) delete the old one. Records are matched by content.
        var create = new List<object> { new { name, type = recordType, rdata = destination, ttl = oldTtl ?? 300 } };
        object body = oldRdata != null
            ? new { autoCommit = true, strict = true, create, delete = new List<object> { new { name, type = recordType, rdata = oldRdata, ttl = oldTtl ?? 300 } } }
            : new { autoCommit = true, strict = true, create };

        using var resp = await client.PostAsJsonAsync($"{Api}/domain/{zoneId.Value}/changeset", body, ct);
        var (ok, _, err) = await ReadEnvelopeAsync(resp, ct);
        return ok
            ? (true, $"{(oldRdata != null ? "updated" : "created")} {fqdn} {recordType} -> {destination}")
            : (false, err);
    }

    private async Task<(long? id, bool? managed, string? error)> FindDomainAsync(HttpClient client, string zone, CancellationToken ct)
    {
        using var resp = await client.GetAsync($"{Api}/domain?fqdn={Uri.EscapeDataString(zone)}", ct);
        var (ok, result, err) = await ReadEnvelopeAsync(resp, ct);
        if (!ok) return (null, null, err);
        if (result.ValueKind != JsonValueKind.Array) return (null, null, null);
        foreach (var d in result.EnumerateArray())
        {
            var fqdn = d.TryGetProperty("fqdn", out var f) ? f.GetString() : null;
            if (!string.Equals(fqdn, zone, StringComparison.OrdinalIgnoreCase)) continue;
            long? id = d.TryGetProperty("id", out var i) && i.TryGetInt64(out var idv) ? idv : null;
            bool? managed = d.TryGetProperty("isDnsManaged", out var m) ? m.ValueKind == JsonValueKind.True : null;
            return (id, managed, null);
        }
        return (null, null, null);
    }

    private async Task<(string? revId, string? error)> ActiveRevisionAsync(HttpClient client, long domainId, CancellationToken ct)
    {
        using var resp = await client.GetAsync($"{Api}/domain/{domainId}/dns/revision", ct);
        var (ok, result, err) = await ReadEnvelopeAsync(resp, ct);
        if (!ok) return (null, err);
        if (result.ValueKind == JsonValueKind.Array)
            foreach (var r in result.EnumerateArray())
                if (r.TryGetProperty("state", out var s) && s.GetString() == "active" && r.TryGetProperty("identifier", out var id))
                    return (id.GetString(), null);
        return (null, "no active DNS revision");
    }

    private async Task<(bool ok, string? rdata, int? ttl, string? error)> FindRecordAsync(
        HttpClient client, long domainId, string revId, string name, string type, CancellationToken ct)
    {
        var basePath = $"{Api}/domain/{domainId}/dns/revision/{Uri.EscapeDataString(revId)}/record";
        string? url = basePath;
        while (url != null)
        {
            using var resp = await client.GetAsync(url, ct);
            var (ok, result, err, nextPage) = await ReadPageAsync(resp, ct);
            if (!ok) return (false, null, null, err);
            if (result.ValueKind == JsonValueKind.Array)
                foreach (var rec in result.EnumerateArray())
                {
                    var rn = rec.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var rt = rec.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (string.Equals(rn, name, StringComparison.OrdinalIgnoreCase) && string.Equals(rt, type, StringComparison.OrdinalIgnoreCase))
                    {
                        var rdata = rec.TryGetProperty("rdata", out var rd) ? rd.GetString() : null;
                        int? ttl = rec.TryGetProperty("ttl", out var tt) && tt.ValueKind == JsonValueKind.Number ? tt.GetInt32() : null;
                        return (true, rdata, ttl, null);
                    }
                }
            url = nextPage > 0 ? $"{basePath}?page={nextPage}" : null;
        }
        return (true, null, null, null); // walked every page, record not present
    }

    private static async Task<(bool ok, JsonElement result, string error)> ReadEnvelopeAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var (ok, result, err, _) = await ReadPageAsync(resp, ct);
        return (ok, result, err);
    }

    // netcup wraps every response: { success, errors:[{code,message}], meta.pagination.nextPage, result }.
    // A logical error can come back with HTTP 200 and success:false, so check both; use errors[0] for the message.
    private static async Task<(bool ok, JsonElement result, string error, int nextPage)> ReadPageAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        JsonDocument doc;
        try { doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct); }
        catch { return (false, default, $"HTTP {(int)resp.StatusCode}: invalid response from netcup", 0); }
        using (doc)
        {
            var root = doc.RootElement;
            var success = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
            if (!success || !resp.IsSuccessStatusCode)
            {
                var msg = $"HTTP {(int)resp.StatusCode}";
                if (root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0)
                {
                    var e0 = errs[0];
                    var m = e0.TryGetProperty("message", out var mm) ? mm.GetString() : null;
                    var code = e0.TryGetProperty("code", out var cc) ? cc.GetString() : null;
                    if (!string.IsNullOrEmpty(m)) msg = code != null ? $"{m} ({code})" : m;
                    else if (!string.IsNullOrEmpty(code)) msg = code!;
                }
                return (false, default, msg, 0);
            }
            var result = root.TryGetProperty("result", out var r) ? r.Clone() : default;
            var next = 0;
            if (root.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object
                && meta.TryGetProperty("pagination", out var pg) && pg.ValueKind == JsonValueKind.Object
                && pg.TryGetProperty("nextPage", out var np) && np.ValueKind == JsonValueKind.Number)
                next = np.GetInt32();
            return (true, result, "", next);
        }
    }
}
