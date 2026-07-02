using System.Text.Json;
using Matddns.Models;

namespace Matddns.Services;

/// <summary>Talks to another Matddns instance over its JSON API: push an IP to /api/update, or pull a peer's source entries from /api/source.</summary>
public class MatddnsClient
{
    public record RemoteEntry(string Key, string Label, string? Ipv4, string? Ipv6, string? Error);

    private readonly IHttpClientFactory _http;
    public MatddnsClient(IHttpClientFactory http) => _http = http;

    // ---- target side: push this instance's chosen IP to the remote ----
    public async Task<(bool ok, string message)> PushAsync(MatddnsLinkSettings cfg, string hostname, string? ipv4, string? ipv6, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.BaseUrl)) return (false, "base URL missing");
        if (string.IsNullOrWhiteSpace(cfg.Token)) return (false, "token missing");

        var qs = $"token={Uri.EscapeDataString(cfg.Token.Trim())}";
        if (!string.IsNullOrWhiteSpace(hostname)) qs += $"&hostname={Uri.EscapeDataString(hostname)}";
        if (!string.IsNullOrWhiteSpace(ipv4)) qs += $"&ipv4={Uri.EscapeDataString(ipv4)}";
        if (!string.IsNullOrWhiteSpace(ipv6)) qs += $"&ipv6={Uri.EscapeDataString(ipv6)}";

        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        using var resp = await client.GetAsync($"{Base(cfg.BaseUrl)}/api/update?{qs}", ct);

        string? status = null, changed = null;
        try
        {
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
                if (doc.RootElement.TryGetProperty("changed", out var c) && (c.ValueKind == JsonValueKind.True || c.ValueKind == JsonValueKind.False))
                    changed = c.GetBoolean() ? "changed" : "unchanged";
            }
        }
        catch { /* non-JSON body */ }

        if (resp.IsSuccessStatusCode && status == "ok")
            return (true, $"{hostname} -> {string.Join("/", new[] { ipv4, ipv6 }.Where(x => !string.IsNullOrEmpty(x)))} ({changed ?? "ok"})");
        return (false, status ?? $"HTTP {(int)resp.StatusCode}");
    }

    // ---- source side: pull a peer's source entries ----
    public async Task<List<RemoteEntry>> PullAsync(MatddnsLinkSettings cfg, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.BaseUrl)) throw new InvalidOperationException("base URL missing");
        if (string.IsNullOrWhiteSpace(cfg.Token)) throw new InvalidOperationException("token missing");

        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        using var resp = await client.GetAsync($"{Base(cfg.BaseUrl)}/api/source?token={Uri.EscapeDataString(cfg.Token.Trim())}", ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized || resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            throw new InvalidOperationException($"{(int)resp.StatusCode} — token rejected by the remote instance.");
        resp.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var result = new List<RemoteEntry>();
        if (doc.RootElement.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
            foreach (var src in sources.EnumerateArray())
            {
                var sname = Str(src, "name") ?? "remote";
                if (!src.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) continue;
                foreach (var e in entries.EnumerateArray())
                {
                    var label = Str(e, "label") ?? "entry";
                    var key = $"{sname}/{label}";
                    result.Add(new RemoteEntry(key, $"{sname} · {label}", Str(e, "ip"), Str(e, "ipv6"), Str(e, "error")));
                }
            }
        return result;
    }

    private static string Base(string url) => url.Trim().TrimEnd('/');

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
