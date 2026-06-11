using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Matddns.Models;

namespace Matddns.Services;

public class DynDnsClient
{
    private readonly IHttpClientFactory _http;

    public DynDnsClient(IHttpClientFactory http) => _http = http;

    public async Task<(bool ok, string response)> UpdateAsync(DynDnsSettings settings, string hostname, string ip, CancellationToken ct)
    {
        // A rule writes one record (A or AAAA), so exactly one family is set; fill the matching
        // placeholder and leave the other empty (empty query params are dropped below).
        var isV6 = IPAddress.TryParse(ip, out var addr) && addr.AddressFamily == AddressFamily.InterNetworkV6;
        var ipv4 = isV6 ? "" : ip;
        var ipv6 = isV6 ? ip : "";

        var url = settings.UpdateUrl
            .Replace("{hostname}", Uri.EscapeDataString(hostname), StringComparison.OrdinalIgnoreCase)
            .Replace("{ipv4}", Uri.EscapeDataString(ipv4), StringComparison.OrdinalIgnoreCase)
            .Replace("{ipv6}", Uri.EscapeDataString(ipv6), StringComparison.OrdinalIgnoreCase)
            .Replace("{ip}", Uri.EscapeDataString(ip), StringComparison.OrdinalIgnoreCase)
            .Replace("{user}", Uri.EscapeDataString(settings.Username), StringComparison.OrdinalIgnoreCase)
            .Replace("{password}", Uri.EscapeDataString(settings.Password), StringComparison.OrdinalIgnoreCase);

        url = DropEmptyQueryParams(url);

        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Matddns/1.0");

        if (!string.IsNullOrEmpty(settings.Username))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Username}:{settings.Password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }

        try
        {
            using var resp = await client.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            var ok = resp.IsSuccessStatusCode &&
                     !body.Contains("badauth", StringComparison.OrdinalIgnoreCase) &&
                     !body.Contains("nohost", StringComparison.OrdinalIgnoreCase) &&
                     !body.Contains("abuse", StringComparison.OrdinalIgnoreCase) &&
                     !body.Contains("911", StringComparison.OrdinalIgnoreCase);
            return (ok, body.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // Removes query params with an empty value, e.g. an A-record push leaves "&myipv6=" -> dropped,
    // so the unused family is never sent (some providers would clear the record otherwise).
    private static string DropEmptyQueryParams(string url)
    {
        var q = url.IndexOf('?');
        if (q < 0) return url;
        var kept = url[(q + 1)..]
            .Split('&')
            .Where(p => p.Length > 0 && !p.EndsWith("=", StringComparison.Ordinal))
            .ToArray();
        return kept.Length > 0 ? $"{url[..q]}?{string.Join("&", kept)}" : url[..q];
    }
}
