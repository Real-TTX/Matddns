using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Matddns.Models;

namespace Matddns.Services;

/// <summary>Reads the WAN IPv4/IPv6 from a FRITZ!Box via TR-064 (SOAP). Works locally (fritz.box:49000) or remotely via MyFRITZ!.</summary>
public class FritzboxClient
{
    public record FritzWan(string? Ipv4, string? Ipv6);

    public async Task<FritzWan> GetWanAsync(FritzboxSettings cfg, CancellationToken ct)
    {
        using var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(cfg.Username, cfg.Password), // TR-064 uses HTTP Digest; HttpClient handles the challenge
            ServerCertificateCustomValidationCallback = cfg.IgnoreCertificate ? (_, _, _, _) => true : null
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        var baseUrl = NormalizeBase(cfg.BaseUrl);

        var v4 = await TryAsync(http, baseUrl, "GetExternalIPAddress", "NewExternalIPAddress", ct);
        var v6 = await TryAsync(http, baseUrl, "X_AVM-DE_GetExternalIPv6Address", "NewExternalIPv6Address", ct);
        if (v4 == null && v6 == null)
            throw new InvalidOperationException("No IP from TR-064 — check host/credentials and that TR-064 is enabled on the FRITZ!Box.");
        return new FritzWan(Clean(v4), Clean(v6));
    }

    // Try the action on WANPPPConnection (DSL/PPPoE), then WANIPConnection (cable/DHCP).
    private static async Task<string?> TryAsync(HttpClient http, string baseUrl, string action, string resultTag, CancellationToken ct)
    {
        foreach (var (svc, path) in new[]
        {
            ("urn:dslforum-org:service:WANPPPConnection:1", "/upnp/control/wanpppconn1"),
            ("urn:dslforum-org:service:WANIPConnection:1", "/upnp/control/wanipconnection1")
        })
        {
            try
            {
                var body = $"<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><u:{action} xmlns:u=\"{svc}\"/></s:Body></s:Envelope>";
                using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + path) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };
                req.Headers.TryAddWithoutValidation("SOAPAction", $"{svc}#{action}");
                using var resp = await http.SendAsync(req, ct);
                var xml = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode) continue;
                var m = Regex.Match(xml, $"<{Regex.Escape(resultTag)}>(.*?)</{Regex.Escape(resultTag)}>", RegexOptions.Singleline);
                if (m.Success) return m.Groups[1].Value.Trim();
            }
            catch { /* try the next service */ }
        }
        return null;
    }

    private static string NormalizeBase(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (string.IsNullOrEmpty(s)) s = "http://fritz.box:49000";
        if (!s.Contains("://", StringComparison.Ordinal)) s = "http://" + s;
        return s.TrimEnd('/');
    }

    private static string? Clean(string? s) =>
        string.IsNullOrWhiteSpace(s) || s == "0.0.0.0" || s == "::" ? null : s.Trim();
}
