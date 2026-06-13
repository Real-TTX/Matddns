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
        var resp = await client.PostAsJsonAsync(Endpoint, req, ct);
        var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var status = doc.RootElement.GetProperty("status").GetString();
        if (status != "success")
        {
            var msg = doc.RootElement.TryGetProperty("longmessage", out var lm) ? lm.GetString() : "login failed";
            return ("", msg);
        }
        var sid = doc.RootElement.GetProperty("responsedata").GetProperty("apisessionid").GetString();
        return (sid ?? "", null);
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
        try { await client.PostAsJsonAsync(Endpoint, req, ct); } catch { /* ignore */ }
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
        if (!string.IsNullOrEmpty(err)) return (false, $"login: {err}");

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
            var infoResp = await client.PostAsJsonAsync(Endpoint, infoReq, ct);
            var infoDoc = await JsonDocument.ParseAsync(await infoResp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (infoDoc.RootElement.GetProperty("status").GetString() != "success")
            {
                var msg = infoDoc.RootElement.TryGetProperty("longmessage", out var lm) ? lm.GetString() : "infoDnsRecords failed";
                return (false, msg ?? "infoDnsRecords failed");
            }

            var records = infoDoc.RootElement.GetProperty("responsedata").GetProperty("dnsrecords");
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
                    return (false, $"record {hostname}.{domain} {recordType} does not exist (enable dynamic records on this zone to auto-create)");
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
            var updResp = await client.PostAsJsonAsync(Endpoint, updReq, ct);
            var updDoc = await JsonDocument.ParseAsync(await updResp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var status = updDoc.RootElement.GetProperty("status").GetString();
            var longMsg = updDoc.RootElement.TryGetProperty("longmessage", out var lm2) ? lm2.GetString() : "";
            if (status == "success")
                return (true, $"updated {hostname} {recordType} -> {destination}");
            return (false, longMsg ?? "updateDnsRecords failed");
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
