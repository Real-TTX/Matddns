using System.Net.Http.Json;
using System.Text.Json;
using Matddns.Models;

namespace Matddns.Services;

public class NetcupClient
{
    private readonly IHttpClientFactory _http;
    private const string Endpoint = "https://ccp.netcup.net/run/webservice/servers/endpoint.php?JSON";

    public NetcupClient(IHttpClientFactory http) => _http = http;

    private async Task<(string sessionId, string? error)> LoginAsync(HttpClient client, NetcupSettings cfg, CancellationToken ct)
    {
        var req = new
        {
            action = "login",
            param = new
            {
                customernumber = cfg.CustomerNumber,
                apikey = cfg.ApiKey,
                apipassword = cfg.ApiPassword
            }
        };
        using var resp = await client.PostAsJsonAsync(Endpoint, req, ct);
        var (rok, doc, rerr) = await ReadAsync(resp, ct);
        if (!rok) return ("", rerr);
        using (doc)
        {
            var status = doc!.RootElement.TryGetProperty("status", out var st) ? st.GetString() : null;
            if (status != "success")
            {
                var msg = doc.RootElement.TryGetProperty("longmessage", out var lm) ? lm.GetString() : null;
                return ("", string.IsNullOrEmpty(msg) ? "login failed" : msg);
            }
            var sid = doc.RootElement.TryGetProperty("responsedata", out var rd) && rd.TryGetProperty("apisessionid", out var si)
                ? si.GetString() : null;
            return (sid ?? "", null);
        }
    }

    // Reads a Netcup JSON response defensively: a non-2xx status or a non-JSON/empty body fails cleanly
    // instead of throwing an uncaught parser exception. Caller owns (and disposes) the returned document.
    private static async Task<(bool ok, JsonDocument? doc, string error)> ReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (!resp.IsSuccessStatusCode) return (false, null, $"HTTP {(int)resp.StatusCode}");
        try
        {
            var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return (true, doc, "");
        }
        catch { return (false, null, "invalid response from Netcup"); }
    }

    private async Task LogoutAsync(HttpClient client, NetcupSettings cfg, string sid, CancellationToken ct)
    {
        var req = new
        {
            action = "logout",
            param = new
            {
                customernumber = cfg.CustomerNumber,
                apikey = cfg.ApiKey,
                apisessionid = sid
            }
        };
        try { using var resp = await client.PostAsJsonAsync(Endpoint, req, ct); } catch { /* ignore */ }
    }

    public async Task<(bool ok, string message)> UpdateRecordAsync(
        NetcupSettings cfg,
        string domain,
        string hostname,
        string recordType,
        string destination,
        bool allowCreate,
        CancellationToken ct)
    {
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);

        var (sid, err) = await LoginAsync(client, cfg, ct);
        if (!string.IsNullOrEmpty(err) || string.IsNullOrEmpty(sid))
            return (false, $"login: {(string.IsNullOrEmpty(err) ? "no session id" : err)}");

        try
        {
            var infoReq = new
            {
                action = "infoDnsRecords",
                param = new
                {
                    domainname = domain,
                    customernumber = cfg.CustomerNumber,
                    apikey = cfg.ApiKey,
                    apisessionid = sid
                }
            };
            using var infoResp = await client.PostAsJsonAsync(Endpoint, infoReq, ct);
            var (infoOk, infoDocN, infoErr) = await ReadAsync(infoResp, ct);
            if (!infoOk) return (false, infoErr);
            using var infoDoc = infoDocN!;
            var infoStatus = infoDoc.RootElement.TryGetProperty("status", out var ist) ? ist.GetString() : null;
            if (infoStatus != "success")
            {
                var msg = infoDoc.RootElement.TryGetProperty("longmessage", out var lm) ? lm.GetString() : null;
                return (false, string.IsNullOrEmpty(msg) ? "infoDnsRecords failed" : msg);
            }

            if (!infoDoc.RootElement.TryGetProperty("responsedata", out var rdata) || !rdata.TryGetProperty("dnsrecords", out var records))
                return (false, "unexpected response (no dnsrecords)");
            var updated = new List<object>();
            var foundExisting = false;
            foreach (var rec in records.EnumerateArray())
            {
                var host = rec.GetProperty("hostname").GetString() ?? "";
                var type = rec.GetProperty("type").GetString() ?? "";
                if (host.Equals(hostname, StringComparison.OrdinalIgnoreCase) && type.Equals(recordType, StringComparison.OrdinalIgnoreCase))
                {
                    foundExisting = true;
                    var current = rec.GetProperty("destination").GetString();
                    if (current == destination)
                        return (true, $"unchanged ({hostname} {recordType} {destination})");

                    updated.Add(new
                    {
                        id = rec.GetProperty("id").GetString(),
                        hostname = host,
                        type,
                        priority = rec.TryGetProperty("priority", out var p) ? p.GetString() : "0",
                        destination,
                        deleterecord = false,
                        state = "yes"
                    });
                }
            }

            if (!foundExisting)
            {
                if (!allowCreate)
                    return (false, "record does not exist");
                updated.Add(new
                {
                    hostname,
                    type = recordType,
                    priority = "0",
                    destination,
                    deleterecord = false,
                    state = "yes"
                });
            }

            var updReq = new
            {
                action = "updateDnsRecords",
                param = new
                {
                    domainname = domain,
                    customernumber = cfg.CustomerNumber,
                    apikey = cfg.ApiKey,
                    apisessionid = sid,
                    dnsrecordset = new { dnsrecords = updated }
                }
            };
            using var updResp = await client.PostAsJsonAsync(Endpoint, updReq, ct);
            var (updOk, updDocN, updErr) = await ReadAsync(updResp, ct);
            if (!updOk) return (false, updErr);
            using var updDoc = updDocN!;
            var status = updDoc.RootElement.TryGetProperty("status", out var ust) ? ust.GetString() : null;
            if (status == "success")
                return (true, $"updated {hostname} {recordType} -> {destination}");
            var longMsg = updDoc.RootElement.TryGetProperty("longmessage", out var lm2) ? lm2.GetString() : null;
            return (false, string.IsNullOrEmpty(longMsg) ? "updateDnsRecords failed" : longMsg);
        }
        finally
        {
            await LogoutAsync(client, cfg, sid, ct);
        }
    }

    public static (string Domain, string Host) SplitHostname(string fqdn)
    {
        var parts = fqdn.Split('.');
        if (parts.Length <= 2) return (fqdn, "@");
        var domain = string.Join('.', parts[^2..]);
        var host = string.Join('.', parts[..^2]);
        return (domain, host);
    }
}
