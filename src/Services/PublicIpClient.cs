namespace Matddns.Services;

public class PublicIpClient
{
    private readonly IHttpClientFactory _http;
    private static readonly string[] Providers =
    {
        "https://api.ipify.org",
        "https://ifconfig.me/ip",
        "https://icanhazip.com"
    };

    public PublicIpClient(IHttpClientFactory http) => _http = http;

    public async Task<string?> GetPublicIpAsync(CancellationToken ct)
    {
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        foreach (var url in Providers)
        {
            try
            {
                var ip = (await client.GetStringAsync(url, ct)).Trim();
                if (System.Net.IPAddress.TryParse(ip, out _)) return ip;
            }
            catch { /* try next */ }
        }
        return null;
    }
}
