using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Matddns.Models;

namespace Matddns.Services;

/// <summary>
/// Updates an A/AAAA/CNAME record via the DNS part of the Hetzner Cloud API (Bearer token of the project that holds the zone).
/// A name and type form an RRSet that is addressed directly: <c>/zones/{zone}/rrsets/{name}/{type}</c>. A change is an action
/// (<c>set_records</c>, <c>change_ttl</c>); the call waits a few seconds for it to finish.
/// </summary>
public class HetznerClient
{
    private const string Api = "https://api.hetzner.cloud/v1";
    private const int Ttl = 60;            // seconds: a record that follows a changing address has to expire quickly
    private const int MaxActionPolls = 10;

    private readonly IHttpClientFactory _http;

    public HetznerClient(IHttpClientFactory http) => _http = http;

    /// <summary>How long to wait between two looks at an action that is still running (the tests shorten it).</summary>
    internal TimeSpan ActionPollInterval { get; set; } = TimeSpan.FromSeconds(1);

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

        var type = recordType.ToUpperInvariant();
        zone = zone.Trim().TrimEnd('.').ToLowerInvariant();
        var name = RelativeName(recordName, zone);
        var fqdn = name == "@" ? zone : $"{name}.{zone}";
        var value = type == "CNAME" ? Absolute(destination) : destination.Trim(); // a name without the final dot would be read relative to the zone

        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiToken.Trim());

        var zoneUrl = $"{Api}/zones/{Uri.EscapeDataString(zone)}";
        var rrsetUrl = $"{zoneUrl}/rrsets/{PathName(name)}/{type}";

        // 1) the RRSet as it is now
        var current = await SendAsync(client, HttpMethod.Get, rrsetUrl, null, ct);
        if (current.Status == HttpStatusCode.NotFound)
        {
            // "not found" is the zone or the RRSet: tell them apart, the cure is different
            var zoneReply = await SendAsync(client, HttpMethod.Get, zoneUrl, null, ct);
            if (zoneReply.Status == HttpStatusCode.NotFound) return (false, $"zone '{zone}' not found for this token (the token has to belong to the project that holds the zone)");
            if (!zoneReply.Ok) return (false, Describe(zoneReply));
            if (!allowCreate) return (false, "record does not exist");
            return await CreateAsync(client, zoneUrl, name, type, value, fqdn, destination, ct);
        }
        if (!current.Ok || !current.Root.TryGetProperty("rrset", out var rrset)) return (false, Describe(current));

        var records = rrset.TryGetProperty("records", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToList() : [];
        if (records.Count > 1) return (false, $"{fqdn} {type} has {records.Count} records; Matddns manages exactly one, remove the others first");

        var currentValue = records.Count == 1 && records[0].TryGetProperty("value", out var v) ? v.GetString() : null;
        if (currentValue != null && SameValue(type, currentValue, value))
            return (true, $"unchanged ({fqdn} {type} {destination})");

        // 2) overwrite the record (set_records replaces the whole set, so the comment is carried over)
        var record = new Dictionary<string, string> { ["value"] = value };
        if (records.Count == 1 && records[0].TryGetProperty("comment", out var comment) && !string.IsNullOrEmpty(comment.GetString()))
            record["comment"] = comment.GetString()!;
        var set = await SendAsync(client, HttpMethod.Post, $"{rrsetUrl}/actions/set_records", new { records = new[] { record } }, ct);
        if (!set.Ok) return (false, Describe(set));
        var failed = await WaitForActionAsync(client, set, ct);
        if (failed != null) return (false, failed);

        // 3) keep the TTL short (the RRSet may have been created with the zone's default)
        var ttlNote = "";
        var ttl = rrset.TryGetProperty("ttl", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : (int?)null;
        if (ttl != Ttl)
        {
            var change = await SendAsync(client, HttpMethod.Post, $"{rrsetUrl}/actions/change_ttl", new { ttl = Ttl }, ct);
            var ttlFailed = change.Ok ? await WaitForActionAsync(client, change, ct) : Describe(change);
            if (ttlFailed != null) ttlNote = $" (TTL stays {(ttl?.ToString() ?? "at the zone default")}: {ttlFailed})";
        }

        return (true, $"updated {fqdn} {type} -> {destination}{ttlNote}");
    }

    private async Task<(bool ok, string message)> CreateAsync(HttpClient client, string zoneUrl, string name, string type, string value, string fqdn, string destination, CancellationToken ct)
    {
        var body = new { name, type, ttl = Ttl, records = new[] { new { value } } };
        var created = await SendAsync(client, HttpMethod.Post, $"{zoneUrl}/rrsets", body, ct);
        if (!created.Ok) return (false, Describe(created));
        var failed = await WaitForActionAsync(client, created, ct);
        return failed != null ? (false, failed) : (true, $"created {fqdn} {type} -> {destination}");
    }

    /// <summary>Waits for the action of a reply to finish. Returns the reason if it failed, otherwise null (also when it is still running after a few seconds: it is applied in the background and the next check sees the result).</summary>
    private async Task<string?> WaitForActionAsync(HttpClient client, Reply reply, CancellationToken ct)
    {
        if (!reply.Root.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.Object) return null;

        for (var attempt = 0; ; attempt++)
        {
            var status = action.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (status == "success") return null;
            if (status == "error") return ActionError(action);
            if (attempt >= MaxActionPolls || !action.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id)) return null;

            await Task.Delay(ActionPollInterval, ct);
            var poll = await SendAsync(client, HttpMethod.Get, $"{Api}/actions/{id}", null, ct);
            // can't tell how it went: do not turn a write that was accepted into a failure
            if (!poll.Ok || !poll.Root.TryGetProperty("action", out action)) return null;
        }
    }

    private static string ActionError(JsonElement action)
    {
        if (action.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object)
        {
            var message = e.TryGetProperty("message", out var m) ? m.GetString() : null;
            var code = e.TryGetProperty("code", out var c) ? c.GetString() : null;
            if (!string.IsNullOrEmpty(message)) return code != null ? $"{message} ({code})" : message;
        }
        return "the action failed";
    }

    private sealed record Reply(HttpStatusCode Status, JsonElement Root, string? Code, string? Message)
    {
        public bool Ok => (int)Status is >= 200 and < 300 && Root.ValueKind == JsonValueKind.Object;
    }

    // Success is a 2xx with a JSON object; an error is {"error":{"code","message","details"}}
    private static async Task<Reply> SendAsync(HttpClient client, HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body != null) request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request, ct);

        JsonElement root = default;
        try
        {
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            root = doc.RootElement.Clone();
        }
        catch (JsonException) { /* empty or not JSON */ }

        string? code = null, message = null;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            code = error.TryGetProperty("code", out var c) ? c.GetString() : null;
            message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        return new Reply(response.StatusCode, root, code, message);
    }

    private static string Describe(Reply reply)
    {
        var text = reply.Message ?? $"HTTP {(int)reply.Status}";
        if (reply.Code != null) text += $" ({reply.Code})";
        return reply.Status switch
        {
            _ when reply.Message == null && (int)reply.Status is >= 200 and < 300 => $"HTTP {(int)reply.Status}: unexpected response (check the API token)",
            HttpStatusCode.Unauthorized => $"{text}: use an API token of the Hetzner Cloud project that holds the zone (tokens of the old DNS Console no longer work)",
            HttpStatusCode.Forbidden => $"{text}: the token needs read & write access",
            HttpStatusCode.TooManyRequests => $"{text}: the rate limit of the project is used up, the next check tries again",
            _ => text
        };
    }

    /// <summary>The name inside the zone, lower case ("@" for the apex). A name that already ends with the zone is shortened.</summary>
    private static string RelativeName(string recordName, string zone)
    {
        var name = recordName.Trim().TrimEnd('.').ToLowerInvariant();
        if (name == zone) return "@";
        if (name.EndsWith("." + zone, StringComparison.Ordinal)) name = name[..^(zone.Length + 1)];
        return name.Length == 0 ? "@" : name;
    }

    // "@" is a valid character of a path; the API reads it as the apex
    private static string PathName(string name) => Uri.EscapeDataString(name).Replace("%40", "@");

    private static string Absolute(string name) => name.Trim().EndsWith('.') ? name.Trim() : name.Trim() + ".";

    /// <summary>Addresses are compared as addresses (the API may return another notation of the same IPv6), names without case and final dot.</summary>
    private static bool SameValue(string type, string a, string b)
    {
        if ((type is "A" or "AAAA") && IPAddress.TryParse(a.Trim(), out var ia) && IPAddress.TryParse(b.Trim(), out var ib)) return ia.Equals(ib);
        return string.Equals(a.Trim().TrimEnd('.'), b.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
    }
}
