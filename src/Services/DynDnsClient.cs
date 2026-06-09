using System.Net.Http.Headers;
using System.Text;
using Matddns.Models;

namespace Matddns.Services;

public class DynDnsClient
{
    private readonly IHttpClientFactory _http;

    public DynDnsClient(IHttpClientFactory http) => _http = http;

    public async Task<(bool ok, string response)> UpdateAsync(DynDnsSettings settings, string hostname, string ip, CancellationToken ct)
    {
        var url = settings.UpdateUrl
            .Replace("{hostname}", Uri.EscapeDataString(hostname), StringComparison.OrdinalIgnoreCase)
            .Replace("{ip}", Uri.EscapeDataString(ip), StringComparison.OrdinalIgnoreCase)
            .Replace("{user}", Uri.EscapeDataString(settings.Username), StringComparison.OrdinalIgnoreCase)
            .Replace("{password}", Uri.EscapeDataString(settings.Password), StringComparison.OrdinalIgnoreCase);

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
}
