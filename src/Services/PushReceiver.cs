using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Matddns.Models;

namespace Matddns.Services;

/// <summary>Structured result of a push update; endpoints render it as JSON (/api/update) or dyndns2 text (/nic/update).</summary>
/// <param name="Status">Canonical status: "ok" | "unauthorized" | "no-ip" | "no-host" | "error".</param>
public record PushOutcome(int HttpStatus, string Status, bool Changed, string? Ipv4, string? Ipv6, string? Message = null);

/// <summary>Receives DynDNS-style pushes. A normal receiver stores the reported IP in its source; a <b>dynamic</b>
/// receiver takes the hostname from the request and writes it straight to the named record in the target Netcup zone.</summary>
public class PushReceiver
{
    private readonly ConfigService _config;
    private readonly LogService _log;
    private readonly NetcupClient _netcup;
    public PushReceiver(ConfigService config, LogService log, NetcupClient netcup)
    {
        _config = config;
        _log = log;
        _netcup = netcup;
    }

    public static string NewToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();

    /// <summary>Constant-time token comparison (avoids a character-timing side channel on the shared secret).</summary>
    public static bool TokenEquals(string? stored, string? provided)
    {
        if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(provided)) return false;
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(stored),
            System.Text.Encoding.UTF8.GetBytes(provided));
    }

    /// <summary>
    /// Accepts explicit per-family values (ipv4/ipv6) and/or a legacy single value (ipAuto, family auto-detected);
    /// falls back to the caller IP. <paramref name="hostname"/> is only used by dynamic receivers.
    /// </summary>
    public async Task<PushOutcome> UpdateAsync(string? token, string? hostname, string? ipv4, string? ipv6, string? ipAuto, string? callerIp, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            _log.Log(LogLevel.Warn, "push", $"rejected: no token supplied (from {callerIp ?? "?"})");
            return new PushOutcome(401, "unauthorized", false, null, null);
        }

        var grp = _config.Read(c => c.Sources.FirstOrDefault(s =>
            s.Kind == SourceKind.Push && s.Push != null && TokenEquals(s.Push.Token, token)));
        if (grp == null)
        {
            _log.Log(LogLevel.Warn, "push", $"rejected: token matches no DynDNS Server source (from {callerIp ?? "?"})");
            return new PushOutcome(401, "unauthorized", false, null, null);
        }

        string? newV4 = null, newV6 = null;
        void Consider(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            s = s.Trim();
            if (!IPAddress.TryParse(s, out var a)) return;
            if (a.IsIPv4MappedToIPv6) { a = a.MapToIPv4(); s = a.ToString(); } // ::ffff:1.2.3.4 -> 1.2.3.4 (dual-stack caller IP)
            if (a.AddressFamily == AddressFamily.InterNetworkV6) newV6 ??= s;
            else newV4 ??= s;
        }
        Consider(ipv4);
        Consider(ipv6);
        Consider(ipAuto);
        if (newV4 == null && newV6 == null) Consider(callerIp); // no explicit IP -> use the caller's

        if (newV4 == null && newV6 == null)
        {
            _log.Log(LogLevel.Warn, $"src:{grp.Name}", $"push had no usable IP address (from {callerIp ?? "?"})");
            return new PushOutcome(400, "no-ip", false, null, null);
        }

        var joined = string.Join(" ", new[] { newV4, newV6 }.Where(x => x != null));

        // a dynamic rule pointing at this source turns it into a wildcard receiver
        var dynRule = _config.Read(c => c.Rules.FirstOrDefault(r => r.Enabled && r.Dynamic && r.DynamicSourceId == grp.Id));
        if (dynRule != null)
            return await DynamicAsync(grp, dynRule, hostname, newV4, newV6, joined, callerIp, ct);

        // normal receiver: store into the single entry; the updater writes it via its rules
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

        _log.Log(LogLevel.Info, $"src:{grp.Name}", $"push from {callerIp ?? "?"}: {joined}{(changed ? " (changed)" : " (unchanged)")}");
        return new PushOutcome(200, "ok", changed, newV4, newV6);
    }

    /// <summary>Dynamic receiver: validate the hostname against the configured namespace, then write the record to Netcup.</summary>
    private async Task<PushOutcome> DynamicAsync(SourceGroup grp, Rule rule, string? hostname, string? newV4, string? newV6, string joined, string? callerIp, CancellationToken ct)
    {
        var host = (hostname ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        var baseFqdn = rule.DynamicBaseFqdn.Trim('.').ToLowerInvariant();
        var zone = rule.DynamicZone.Trim('.').ToLowerInvariant();

        if (string.IsNullOrEmpty(baseFqdn) || string.IsNullOrEmpty(zone) || string.IsNullOrWhiteSpace(rule.DomainGroupId))
            return Reject(grp, "dynamic rule is not fully configured (target zone / namespace)", callerIp);
        if (string.IsNullOrEmpty(host))
            return Reject(grp, "no hostname supplied", callerIp);
        if (!(host == baseFqdn || host.EndsWith("." + baseFqdn, StringComparison.Ordinal)))
        {
            _log.Log(LogLevel.Warn, $"src:{grp.Name}", $"dynamic push rejected: '{host}' is not under {baseFqdn} (from {callerIp ?? "?"})");
            return new PushOutcome(403, "no-host", false, null, null, $"'{host}' is not under {baseFqdn}");
        }

        var recordName = host == zone ? "@"
            : host.EndsWith("." + zone, StringComparison.Ordinal) ? host[..^(zone.Length + 1)]
            : host;

        var dgrp = _config.Read(c => c.Domains.FirstOrDefault(d =>
            d.Id == rule.DomainGroupId && d.Kind == DomainKind.Netcup && d.Netcup != null));
        if (dgrp == null)
            return Reject(grp, "target Netcup zone not found", callerIp);

        bool any = false, changed = false;
        string? err = null;
        if (newV4 != null)
        {
            var (ok, msg) = await _netcup.UpdateRecordAsync(dgrp.Netcup!, zone, recordName, "A", newV4, dgrp.Netcup!.AllowDynamic, ct);
            if (ok) { any = true; if (!msg.StartsWith("unchanged", StringComparison.Ordinal)) changed = true; } else err = msg;
        }
        if (newV6 != null)
        {
            var (ok, msg) = await _netcup.UpdateRecordAsync(dgrp.Netcup!, zone, recordName, "AAAA", newV6, dgrp.Netcup!.AllowDynamic, ct);
            if (ok) { any = true; if (!msg.StartsWith("unchanged", StringComparison.Ordinal)) changed = true; } else err = msg;
        }

        // record the pushed host as a per-hostname entry (for display / state)
        _config.Mutate(c =>
        {
            var g = c.Sources.FirstOrDefault(s => s.Id == grp.Id);
            if (g == null) return;
            var e = g.Entries.FirstOrDefault(x => string.Equals(x.Label, host, StringComparison.OrdinalIgnoreCase));
            if (e == null) { e = new SourceEntry { Label = host }; g.Entries.Add(e); }
            if (newV4 != null) e.CurrentIp = newV4;
            if (newV6 != null) e.CurrentIpv6 = newV6;
            e.LastChecked = DateTime.UtcNow;
            e.LastError = any ? null : err;
        });

        if (!any)
        {
            _log.Log(LogLevel.Error, $"src:{grp.Name}", $"dynamic push {host}: {err}");
            return new PushOutcome(502, "error", false, newV4, newV6, err);
        }
        _log.Log(LogLevel.Info, $"src:{grp.Name}", $"dynamic push {host} -> {joined}{(changed ? " (changed)" : " (unchanged)")}");
        return new PushOutcome(200, "ok", changed, newV4, newV6);
    }

    private PushOutcome Reject(SourceGroup grp, string reason, string? callerIp)
    {
        _log.Log(LogLevel.Warn, $"src:{grp.Name}", $"dynamic push rejected: {reason} (from {callerIp ?? "?"})");
        return new PushOutcome(400, "no-host", false, null, null, reason);
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
