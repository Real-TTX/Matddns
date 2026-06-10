using System.Net;
using System.Net.Sockets;

namespace Matddns.Services;

public class PublicIpClient
{
    private readonly IHttpClientFactory _http;

    private static readonly string[] V4Providers =
    {
        "https://ipv4.icanhazip.com",
        "https://api.ipify.org",
        "https://ifconfig.me/ip"
    };

    private static readonly string[] V6Providers =
    {
        "https://ipv6.icanhazip.com",
        "https://api6.ipify.org"
    };

    public PublicIpClient(IHttpClientFactory http) => _http = http;

    public Task<string?> GetPublicIpAsync(CancellationToken ct) =>
        FetchAsync(V4Providers, AddressFamily.InterNetwork, ct);

    public Task<string?> GetPublicIpv6Async(CancellationToken ct) =>
        FetchAsync(V6Providers, AddressFamily.InterNetworkV6, ct);

    private async Task<string?> FetchAsync(string[] providers, AddressFamily family, CancellationToken ct)
    {
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(6);
        foreach (var url in providers)
        {
            try
            {
                var ip = (await client.GetStringAsync(url, ct)).Trim();
                if (IPAddress.TryParse(ip, out var addr) && addr.AddressFamily == family)
                    return ip;
            }
            catch { /* try next / no connectivity of this family */ }
        }
        return null;
    }
}
