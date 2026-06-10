using System.Net;
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

    /// <summary>dyndns2-style result: (httpStatus, plain-text body like "good 1.2.3.4" / "badauth" / "nohost").</summary>
    public (int Status, string Body) Update(string? token, string? ip, string? callerIp)
    {
        if (string.IsNullOrWhiteSpace(token))
            return (401, "badauth");

        var grp = _config.Read(c => c.Sources.FirstOrDefault(s =>
            s.Kind == SourceKind.Push && s.Push != null && s.Push.Token == token));
        if (grp == null)
            return (401, "badauth");

        var chosen = !string.IsNullOrWhiteSpace(ip) ? ip!.Trim() : callerIp;
        if (string.IsNullOrWhiteSpace(chosen) || !IPAddress.TryParse(chosen, out _))
            return (200, "nohost");

        var changed = false;
        _config.Mutate(c =>
        {
            var g = c.Sources.FirstOrDefault(s => s.Id == grp.Id);
            if (g == null) return;
            if (g.Entries.Count == 0) g.Entries.Add(new SourceEntry { Label = "Pushed IP" });
            var e = g.Entries[0];
            changed = e.CurrentIp != chosen;
            e.Label = "Pushed IP";
            e.CurrentIp = chosen;
            e.LastChecked = DateTime.UtcNow;
            e.LastError = null;
        });

        _log.Log(LogLevel.Debug, $"src:{grp.Name}", $"push update: {chosen}{(changed ? " (changed)" : "")}");
        return (200, $"{(changed ? "good" : "nochg")} {chosen}");
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
