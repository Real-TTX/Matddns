using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Matddns.Models;

namespace Matddns.Services;

/// <summary>Receives DynDNS-style pushes: an external device reports its IP, which is stored in a Push source.</summary>
public class PushReceiver
{
    private readonly ConfigService _config;
    private readonly LogService _log;
    public PushReceiver(ConfigService config, LogService log)
    {
        _config = config;
        _log = log;
    }

    public static string NewToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();

    /// <summary>
    /// Accepts explicit per-family values (ipv4/ipv6) and/or a legacy single value (ipAuto, family auto-detected);
    /// falls back to the caller IP. Each value is routed by its actual address family. dyndns2-style result.
    /// </summary>
    public (int Status, string Body) Update(string? token, string? ipv4, string? ipv6, string? ipAuto, string? callerIp)
    {
        if (string.IsNullOrWhiteSpace(token))
            return (401, "badauth");

        var grp = _config.Read(c => c.Sources.FirstOrDefault(s =>
            s.Kind == SourceKind.Push && s.Push != null && s.Push.Token == token));
        if (grp == null)
            return (401, "badauth");

        string? newV4 = null, newV6 = null;
        void Consider(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            s = s.Trim();
            if (!IPAddress.TryParse(s, out var a)) return;
            if (a.AddressFamily == AddressFamily.InterNetworkV6) newV6 ??= s;
            else newV4 ??= s;
        }
        Consider(ipv4);
        Consider(ipv6);
        Consider(ipAuto);
        if (newV4 == null && newV6 == null) Consider(callerIp); // no explicit IP -> use the caller's

        if (newV4 == null && newV6 == null)
            return (200, "nohost");

        var changed = false;
        _config.Mutate(c =>
        {
            var g = c.Sources.FirstOrDefault(s => s.Id == grp.Id);
            if (g == null) return;
            if (g.Entries.Count == 0) g.Entries.Add(new SourceEntry { Label = "Pushed IP" });
            var e = g.Entries[0];
            // only touch the families that were supplied; keep the other one as-is
            if (newV4 != null) { if (e.CurrentIp != newV4) changed = true; e.CurrentIp = newV4; }
            if (newV6 != null) { if (e.CurrentIpv6 != newV6) changed = true; e.CurrentIpv6 = newV6; }
            e.Label = "Pushed IP";
            e.LastChecked = DateTime.UtcNow;
            e.LastError = null;
        });

        var joined = string.Join(" ", new[] { newV4, newV6 }.Where(x => x != null));
        _log.Log(LogLevel.Debug, $"src:{grp.Name}", $"push update: {joined}{(changed ? " (changed)" : "")}");
        return (200, $"{(changed ? "good" : "nochg")} {joined}");
    }

    /// <summary>Reads the password from an HTTP Basic auth header value, or null.</summary>
    public static string? BasicAuthPassword(string? authHeader)
    {
        if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(authHeader["Basic ".Length..].Trim()));
            var idx = decoded.IndexOf(':');
            return idx >= 0 ? decoded[(idx + 1)..] : decoded;
        }
        catch { return null; }
    }
}
