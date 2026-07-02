using Matddns.Models;

namespace Matddns.Services;

public class UpdaterService : BackgroundService
{
    private readonly ConfigService _config;
    private readonly LogService _log;
    private readonly PublicIpClient _publicIp;
    private readonly DnsLookupClient _dns;
    private readonly FritzboxClient _fritz;
    private readonly UnifiClient _unifi;
    private readonly UnifiCloudClient _unifiCloud;
    private readonly DynDnsClient _dyndns;
    private readonly NetcupClient _netcup;
    private readonly CloudflareClient _cloudflare;
    private readonly HetznerClient _hetzner;
    private readonly GoDaddyClient _godaddy;
    private readonly MatddnsClient _matddns;
    private readonly SourceResolver _resolver;
    private readonly ReachabilityChecker _reach;

    public UpdaterService(
        ConfigService config,
        LogService log,
        PublicIpClient publicIp,
        DnsLookupClient dns,
        FritzboxClient fritz,
        UnifiClient unifi,
        UnifiCloudClient unifiCloud,
        DynDnsClient dyndns,
        NetcupClient netcup,
        CloudflareClient cloudflare,
        HetznerClient hetzner,
        GoDaddyClient godaddy,
        MatddnsClient matddns,
        SourceResolver resolver,
        ReachabilityChecker reach)
    {
        _config = config;
        _log = log;
        _publicIp = publicIp;
        _dns = dns;
        _fritz = fritz;
        _unifi = unifi;
        _unifiCloud = unifiCloud;
        _dyndns = dyndns;
        _netcup = netcup;
        _cloudflare = cloudflare;
        _hetzner = hetzner;
        _godaddy = godaddy;
        _matddns = matddns;
        _resolver = resolver;
        _reach = reach;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.Log(LogLevel.Info, "updater", "Background updater started");
        _log.ApplyRetention();
        var lastRetention = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshSourcesAsync(stoppingToken);
                await RunRulesAsync(stoppingToken);
                if ((DateTime.UtcNow - lastRetention).TotalHours >= 1)
                {
                    _log.ApplyRetention();
                    lastRetention = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                _log.Log(LogLevel.Error, "updater", $"loop: {ex.Message}");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); } catch { }
        }
    }

    private async Task RefreshSourcesAsync(CancellationToken ct)
    {
        var cfg = _config.Current;
        var now = DateTime.UtcNow;

        foreach (var group in cfg.Sources)
        {
            var due = group.Entries.All(e => e.LastChecked == null) ||
                      group.Entries.Any(e => e.LastChecked == null || (now - e.LastChecked.Value).TotalSeconds >= group.IntervalSeconds);
            if (!due) continue;

            if (group.Kind == SourceKind.PublicIp)
            {
                var ip = await _publicIp.GetPublicIpAsync(ct);
                var ipv6 = await _publicIp.GetPublicIpv6Async(ct);
                _config.Mutate(c =>
                {
                    var g = c.Sources.FirstOrDefault(x => x.Id == group.Id);
                    if (g == null) return;
                    if (g.Entries.Count == 0)
                        g.Entries.Add(new SourceEntry { Label = "Public IP" });
                    var entry = g.Entries[0];
                    entry.Label = "Public IP";
                    entry.CurrentIp = ip;
                    entry.CurrentIpv6 = ipv6;
                    entry.LastChecked = DateTime.UtcNow;
                    entry.LastError = (ip == null && ipv6 == null) ? "no IP" : null;
                });
                if (ip != null || ipv6 != null)
                    _log.Log(LogLevel.Debug, $"src:{group.Name}", $"Public IP v4={ip ?? "-"} v6={ipv6 ?? "-"}");
                else
                    _log.Log(LogLevel.Warn, $"src:{group.Name}", "could not resolve public IP");
            }
            else if (group.Kind == SourceKind.Unifi && group.Unifi != null)
            {
                List<UnifiClient.WanInfo> wans;
                try { wans = await _unifi.GetWansAsync(group.Unifi, ct); }
                catch (Exception ex)
                {
                    _log.Log(LogLevel.Error, $"src:{group.Name}", $"Unifi: {ex.Message}");
                    _config.Mutate(c =>
                    {
                        var g = c.Sources.FirstOrDefault(x => x.Id == group.Id);
                        if (g == null) return;
                        foreach (var e in g.Entries)
                        {
                            e.LastChecked = DateTime.UtcNow;
                            e.LastError = ex.Message;
                        }
                    });
                    continue;
                }

                WriteWanEntries(group.Id, wans);
                _log.Log(LogLevel.Debug, $"src:{group.Name}",
                    $"Unifi WANs: {string.Join(", ", wans.Select(w => $"{(string.IsNullOrEmpty(w.DisplayName) ? w.Name : w.DisplayName)} [{w.Name}]={(w.Up ? (w.Ip ?? "no-ip") : "down")}"))}");
            }
            else if (group.Kind == SourceKind.UnifiCloud && group.UnifiCloud != null)
            {
                if (group.Entries.Count == 0) continue; // no gateways selected yet — nothing to poll
                List<UnifiCloudClient.HostInfo> hosts;
                try { hosts = await _unifiCloud.ListHostsAsync(group.UnifiCloud.ApiKey, ct); }
                catch (Exception ex)
                {
                    _log.Log(LogLevel.Error, $"src:{group.Name}", $"Unifi cloud: {ex.Message}");
                    _config.Mutate(c =>
                    {
                        var g = c.Sources.FirstOrDefault(x => x.Id == group.Id);
                        if (g == null) return;
                        foreach (var e in g.Entries) { e.LastChecked = DateTime.UtcNow; e.LastError = ex.Message; }
                    });
                    continue;
                }
                var byId = hosts.ToDictionary(h => h.Id, h => h, StringComparer.Ordinal);
                _config.Mutate(c =>
                {
                    var g = c.Sources.FirstOrDefault(x => x.Id == group.Id);
                    if (g == null) return;
                    foreach (var e in g.Entries)
                    {
                        if (e.InterfaceName != null && byId.TryGetValue(e.InterfaceName, out var h))
                        {
                            // keep the user's alias (Label) — only refresh the IPs
                            e.CurrentIp = h.Ipv4;
                            e.CurrentIpv6 = h.Ipv6;
                            e.LastChecked = DateTime.UtcNow;
                            e.LastError = (h.Ipv4 == null && h.Ipv6 == null) ? "no public IP" : null;
                        }
                        else
                        {
                            e.LastChecked = DateTime.UtcNow;
                            e.LastError = "gateway not found";
                        }
                    }
                });
                _log.Log(LogLevel.Debug, $"src:{group.Name}",
                    $"Unifi cloud: {string.Join(", ", group.Entries.Select(e => $"{e.Label}={(byId.TryGetValue(e.InterfaceName ?? "", out var h) ? (h.Ipv4 ?? h.Ipv6 ?? "no-ip") : "missing")}"))}");
            }
            else if (group.Kind == SourceKind.Matddns && group.Matddns != null)
            {
                List<MatddnsClient.RemoteEntry> remote;
                try { remote = await _matddns.PullAsync(group.Matddns, ct); }
                catch (Exception ex)
                {
                    _log.Log(LogLevel.Error, $"src:{group.Name}", $"Matddns peer: {ex.Message}");
                    _config.Mutate(c =>
                    {
                        var g = c.Sources.FirstOrDefault(x => x.Id == group.Id);
                        if (g == null) return;
                        foreach (var e in g.Entries) { e.LastChecked = DateTime.UtcNow; e.LastError = ex.Message; }
                    });
                    continue;
                }
                _config.Mutate(c =>
                {
                    var g = c.Sources.FirstOrDefault(x => x.Id == group.Id);
                    if (g == null) return;
                    var keys = remote.Select(r => r.Key).ToHashSet();
                    foreach (var re in remote)
                    {
                        var match = g.Entries.FirstOrDefault(e => e.InterfaceName == re.Key);
                        if (match == null) { match = new SourceEntry { InterfaceName = re.Key }; g.Entries.Add(match); }
                        match.Label = re.Label;
                        match.CurrentIp = re.Ipv4;
                        match.CurrentIpv6 = re.Ipv6;
                        match.LastChecked = DateTime.UtcNow;
                        match.LastError = re.Error ?? ((re.Ipv4 == null && re.Ipv6 == null) ? "no IP" : null);
                    }
                    // entries that vanished from the peer: keep them (rules may reference them) but flag
                    foreach (var e in g.Entries.Where(e => e.InterfaceName != null && !keys.Contains(e.InterfaceName)))
                    {
                        e.LastChecked = DateTime.UtcNow;
                        e.LastError = "not on remote";
                    }
                });
                _log.Log(LogLevel.Debug, $"src:{group.Name}", $"Matddns peer: {remote.Count} entr{(remote.Count == 1 ? "y" : "ies")}");
            }
            else if (group.Kind == SourceKind.Static)
            {
                _config.Mutate(c =>
                {
                    var g = c.Sources.FirstOrDefault(x => x.Id == group.Id);
                    if (g == null) return;
                    var ip = g.Static?.Ip;
                    var ipv6 = g.Static?.Ipv6;
                    if (g.Entries.Count == 0)
                        g.Entries.Add(new SourceEntry { Label = "Static IP" });
                    var entry = g.Entries[0];
                    entry.Label = "Static IP";
                    entry.CurrentIp = string.IsNullOrWhiteSpace(ip) ? null : ip!.Trim();
                    entry.CurrentIpv6 = string.IsNullOrWhiteSpace(ipv6) ? null : ipv6!.Trim();
                    entry.LastChecked = DateTime.UtcNow;
                    entry.LastError = (string.IsNullOrWhiteSpace(ip) && string.IsNullOrWhiteSpace(ipv6)) ? "no IP configured" : null;
                });
            }
            else if (group.Kind == SourceKind.Dns)
            {
                var host = group.Dns?.Hostname ?? "";
                var (v4, v6) = await _dns.ResolveAsync(host, ct);
                _config.Mutate(c =>
                {
                    var g = c.Sources.FirstOrDefault(x => x.Id == group.Id);
                    if (g == null) return;
                    if (g.Entries.Count == 0) g.Entries.Add(new SourceEntry());
                    var entry = g.Entries[0];
                    entry.Label = string.IsNullOrWhiteSpace(host) ? "DNS lookup" : host;
                    entry.CurrentIp = v4;
                    entry.CurrentIpv6 = v6;
                    entry.LastChecked = DateTime.UtcNow;
                    entry.LastError = (v4 == null && v6 == null)
                        ? (string.IsNullOrWhiteSpace(host) ? "no hostname configured" : "could not resolve")
                        : null;
                });
                if (v4 != null || v6 != null)
                    _log.Log(LogLevel.Debug, $"src:{group.Name}", $"DNS {host} v4={v4 ?? "-"} v6={v6 ?? "-"}");
                else
                    _log.Log(LogLevel.Warn, $"src:{group.Name}", $"could not resolve {host}");
            }
            else if (group.Kind == SourceKind.Fritzbox && group.Fritzbox != null)
            {
                string? fv4 = null, fv6 = null, ferr = null;
                try { var w = await _fritz.GetWanAsync(group.Fritzbox, ct); fv4 = w.Ipv4; fv6 = w.Ipv6; }
                catch (Exception ex) { ferr = ex.Message; }
                _config.Mutate(c =>
                {
                    var g = c.Sources.FirstOrDefault(x => x.Id == group.Id);
                    if (g == null) return;
                    if (g.Entries.Count == 0) g.Entries.Add(new SourceEntry { Label = "FRITZ!Box WAN" });
                    var entry = g.Entries[0];
                    entry.Label = "FRITZ!Box WAN";
                    if (ferr == null) { entry.CurrentIp = fv4; entry.CurrentIpv6 = fv6; }
                    entry.LastChecked = DateTime.UtcNow;
                    entry.LastError = ferr ?? ((fv4 == null && fv6 == null) ? "no IP" : null);
                });
                if (ferr == null) _log.Log(LogLevel.Debug, $"src:{group.Name}", $"FRITZ!Box v4={fv4 ?? "-"} v6={fv6 ?? "-"}");
                else _log.Log(LogLevel.Error, $"src:{group.Name}", $"FRITZ!Box: {ferr}");
            }
        }
    }

    // Upsert one source entry per WAN (matched by interface group name). Shared by the local Unifi and Unifi-cloud sources.
    private void WriteWanEntries(string groupId, List<UnifiClient.WanInfo> wans)
    {
        _config.Mutate(c =>
        {
            var g = c.Sources.FirstOrDefault(x => x.Id == groupId);
            if (g == null) return;
            foreach (var wan in wans)
            {
                var match = g.Entries.FirstOrDefault(e =>
                    e.InterfaceName != null &&
                    e.InterfaceName.Equals(wan.Name, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    match = new SourceEntry { InterfaceName = wan.Name };
                    g.Entries.Add(match);
                }
                // take the display name from Unifi (label follows the Unifi configuration)
                if (!string.IsNullOrWhiteSpace(wan.DisplayName))
                    match.Label = wan.DisplayName!;
                else if (string.IsNullOrWhiteSpace(match.Label))
                    match.Label = wan.Name.ToUpperInvariant();
                match.CurrentIp = wan.Ip;
                match.CurrentIpv6 = wan.Ipv6;
                match.LastChecked = DateTime.UtcNow;
                match.LastError = wan.Up ? (string.IsNullOrEmpty(wan.Ip) && string.IsNullOrEmpty(wan.Ipv6) ? "no lease" : null) : "down";
            }
        });
    }

    private async Task RunRulesAsync(CancellationToken ct)
    {
        var cfg = _config.Current;
        var now = DateTime.UtcNow;
        foreach (var rule in cfg.Rules)
        {
            if (!rule.Enabled) continue;
            if (rule.Dynamic) continue; // push-driven; written by PushReceiver, not polled here
            var signature = SourceSignature(rule, cfg);
            if (!RuleDue(rule, now, signature)) continue;

            try { await RunSingleRuleAsync(rule, cfg, ct); }
            catch (Exception ex)
            {
                _log.Log(LogLevel.Error, $"rule:{RuleDesc(rule, cfg)}", ex.Message);
                _config.Mutate(c =>
                {
                    var r = c.Rules.FirstOrDefault(x => x.Id == rule.Id);
                    if (r != null) { r.LastRun = DateTime.UtcNow; r.LastResult = "error: " + ex.Message; }
                });
            }
            // remember the source-IP snapshot we just evaluated, so OnChange fires only on the next real change
            _config.Mutate(c =>
            {
                var r = c.Rules.FirstOrDefault(x => x.Id == rule.Id);
                if (r != null) r.LastSourceSignature = signature;
            });
        }
    }

    // Concatenated current IPs of the rule's sources; changes when any source IP changes.
    private static string SourceSignature(Rule rule, AppConfig cfg)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var sid in rule.SourceEntryIdsInOrder)
        {
            var e = cfg.Sources.SelectMany(s => s.Entries).FirstOrDefault(x => x.Id == sid);
            sb.Append(e?.CurrentIp ?? "-").Append('/').Append(e?.CurrentIpv6 ?? "-").Append(';');
        }
        return sb.ToString();
    }

    private async Task RunSingleRuleAsync(Rule rule, AppConfig cfg, CancellationToken ct)
    {
        var dgroup = cfg.Domains.FirstOrDefault(d => d.Id == rule.DomainGroupId);
        var dentry = dgroup?.Entries.FirstOrDefault(e => e.Id == rule.DomainEntryId);
        if (dgroup == null || dentry == null)
        {
            if (rule.LastResult != "target missing")
            {
                _log.Log(LogLevel.Warn, $"rule:{RuleDesc(rule, cfg)}", "domain/entry not found");
                _config.Mutate(c =>
                {
                    var r = c.Rules.FirstOrDefault(x => x.Id == rule.Id);
                    if (r != null) { r.LastRun = DateTime.UtcNow; r.LastResult = "target missing"; }
                });
            }
            return;
        }

        string? chosenValue = null;
        string? chosenSourceId = null;
        int chosenIndex = -1;

        for (int i = 0; i < rule.SourceEntryIdsInOrder.Count; i++)
        {
            var sid = rule.SourceEntryIdsInOrder[i];
            var resolved = _resolver.Find(cfg, sid);
            if (resolved == null) continue;

            // pick the address matching the record family (A=IPv4, AAAA=IPv6; CNAME uses any IP just for reachability)
            var familyIp = dentry.Type switch
            {
                DnsRecordType.AAAA => resolved.Entry.CurrentIpv6,
                DnsRecordType.A => resolved.Entry.CurrentIp,
                _ => resolved.Entry.CurrentIp ?? resolved.Entry.CurrentIpv6
            };
            if (string.IsNullOrWhiteSpace(familyIp)) continue; // source has no usable address for this record type

            string? candidateValue;
            if (dentry.Type == DnsRecordType.CNAME)
            {
                if (i >= rule.CnameTargets.Count) continue;
                var target = rule.CnameTargets[i];
                if (string.IsNullOrWhiteSpace(target)) continue;
                candidateValue = target;
            }
            else
            {
                candidateValue = familyIp;
            }

            // failover validation: accept the source only if it passes ALL enabled checks (ping and/or TCP).
            if (rule.ValidatePing || rule.ValidateTcp)
            {
                var reachable = true;
                if (rule.ValidatePing && !await _reach.PingAsync(familyIp!)) reachable = false;
                if (reachable && rule.ValidateTcp && !await _reach.TcpAsync(familyIp!, rule.ValidationPort, ct)) reachable = false;
                var how = string.Join("+", new[] { rule.ValidatePing ? "Ping" : null, rule.ValidateTcp ? $"TCP:{rule.ValidationPort}" : null }.Where(x => x != null));
                _log.Log(LogLevel.Debug, $"rule:{RuleDesc(rule, cfg)}",
                    $"check {resolved.Entry.Label} {familyIp} ({how}) -> {(reachable ? "ok" : "fail")}");
                if (!reachable) continue;
            }

            chosenValue = candidateValue;
            chosenSourceId = sid;
            chosenIndex = i;
            break;
        }

        if (chosenValue == null)
        {
            // only log on state change (otherwise spam with OnChange every 15s)
            if (rule.LastResult != "no source")
                _log.Log(LogLevel.Warn, $"rule:{RuleDesc(rule, cfg)}", "no reachable source");
            _config.Mutate(c =>
            {
                var r = c.Rules.FirstOrDefault(x => x.Id == rule.Id);
                if (r != null) { r.LastRun = DateTime.UtcNow; r.LastResult = "no source"; }
            });
            return;
        }

        if (rule.LastValue == chosenValue && rule.LastUsedSourceId == chosenSourceId &&
            rule.LastResult != null && rule.LastResult.StartsWith("ok", StringComparison.OrdinalIgnoreCase))
        {
            _config.Mutate(c =>
            {
                var r = c.Rules.FirstOrDefault(x => x.Id == rule.Id);
                if (r != null) r.LastRun = DateTime.UtcNow;
            });
            return;
        }

        string recordType = dentry.Type.ToString();

        bool ok;
        string msg;
        if (dgroup.Kind == DomainKind.DynDns && dgroup.DynDns != null)
        {
            if (dentry.Type == DnsRecordType.CNAME)
            {
                ok = false;
                msg = "CNAME not supported via DynDNS update URL";
            }
            else
            {
                (ok, msg) = await _dyndns.UpdateAsync(dgroup.DynDns, dentry.Hostname, chosenValue, ct);
            }
        }
        else if (dgroup.Kind == DomainKind.Netcup && dgroup.Netcup != null)
        {
            string domain, host;
            if (!string.IsNullOrWhiteSpace(dentry.Domain))
            {
                domain = dentry.Domain!;
                host = string.IsNullOrWhiteSpace(dentry.RecordName) ? "@" : dentry.RecordName!;
            }
            else
            {
                (domain, host) = NetcupClient.SplitHostname(dentry.Hostname);
                if (!string.IsNullOrWhiteSpace(dentry.RecordName)) host = dentry.RecordName!;
            }
            (ok, msg) = await _netcup.UpdateRecordAsync(dgroup.Netcup, domain, host, recordType, chosenValue, dgroup.Netcup.AllowDynamic, ct);
        }
        else if (dgroup.Kind == DomainKind.Cloudflare && dgroup.Cloudflare != null)
        {
            var zone = !string.IsNullOrWhiteSpace(dentry.Domain) ? dentry.Domain! : NetcupClient.SplitHostname(dentry.Hostname).Domain;
            (ok, msg) = await _cloudflare.UpdateRecordAsync(dgroup.Cloudflare, zone, dentry.Hostname, recordType, chosenValue, dgroup.Cloudflare.AllowDynamic, ct);
        }
        else if (dgroup.Kind == DomainKind.Hetzner && dgroup.Hetzner != null)
        {
            var (zone, host) = ZoneAndHost(dentry);
            (ok, msg) = await _hetzner.UpdateRecordAsync(dgroup.Hetzner, zone, host, recordType, chosenValue, dgroup.Hetzner.AllowDynamic, ct);
        }
        else if (dgroup.Kind == DomainKind.GoDaddy && dgroup.GoDaddy != null)
        {
            var (zone, host) = ZoneAndHost(dentry);
            (ok, msg) = await _godaddy.UpdateRecordAsync(dgroup.GoDaddy, zone, host, recordType, chosenValue, dgroup.GoDaddy.AllowDynamic, ct);
        }
        else if (dgroup.Kind == DomainKind.Matddns && dgroup.Matddns != null)
        {
            if (dentry.Type == DnsRecordType.CNAME)
            {
                ok = false; msg = "CNAME not supported via Matddns push";
            }
            else
            {
                var v4 = dentry.Type == DnsRecordType.A ? chosenValue : null;
                var v6 = dentry.Type == DnsRecordType.AAAA ? chosenValue : null;
                (ok, msg) = await _matddns.PushAsync(dgroup.Matddns, dentry.Hostname, v4, v6, ct);
            }
        }
        else
        {
            ok = false; msg = "no credentials configured";
        }

        _log.Log(ok ? LogLevel.Update : LogLevel.Error, $"rule:{RuleDesc(rule, cfg)}",
            $"{recordType} {dentry.Hostname} -> {chosenValue} (src#{chosenIndex}) : {msg}");

        _config.Mutate(c =>
        {
            var r = c.Rules.FirstOrDefault(x => x.Id == rule.Id);
            if (r == null) return;
            r.LastRun = DateTime.UtcNow;
            r.LastResult = ok ? "ok: " + msg : "err: " + msg;
            // a successful write = the moment it was set (happens only on change due to the skip logic)
            if (ok) r.LastChange = DateTime.UtcNow;
            r.LastValue = chosenValue;
            r.LastUsedSourceId = chosenSourceId;
        });
    }

    private static bool RuleDue(Rule rule, DateTime now, string signature)
    {
        if (rule.LastRun == null) return true; // first evaluation after (re)start
        var age = (now - rule.LastRun.Value).TotalSeconds;

        // back off briefly after an error so a misconfiguration doesn't flood the DNS API
        var lastErr = rule.LastResult != null && rule.LastResult.StartsWith("err", StringComparison.OrdinalIgnoreCase);
        if (lastErr && age < 30) return false;

        // time-driven: periodic re-check — re-validates reachability, so a source going DOWN
        // (without its IP changing) still triggers failover.
        if (rule.IntervalSeconds > 0 && age >= rule.IntervalSeconds) return true;

        // event-driven: a source IP changed since the last evaluation.
        if (rule.OnChange && signature != (rule.LastSourceSignature ?? "")) return true;

        return false;
    }

    // Zone + relative record name for zone-based APIs (Hetzner/GoDaddy). Prefers the explicit Domain/RecordName, else derives from the FQDN.
    private static (string zone, string host) ZoneAndHost(DomainEntry de)
    {
        if (!string.IsNullOrWhiteSpace(de.Domain))
            return (de.Domain!, string.IsNullOrWhiteSpace(de.RecordName) ? "@" : de.RecordName!);
        var (domain, host) = NetcupClient.SplitHostname(de.Hostname);
        if (!string.IsNullOrWhiteSpace(de.RecordName)) host = de.RecordName!;
        return (domain, host);
    }

    private static string RuleDesc(Rule rule, AppConfig cfg)
    {
        var dg = cfg.Domains.FirstOrDefault(d => d.Id == rule.DomainGroupId);
        var de = dg?.Entries.FirstOrDefault(e => e.Id == rule.DomainEntryId);
        var act = (de?.Type ?? DnsRecordType.A).ToString();
        var host = de?.Hostname ?? rule.Id[..Math.Min(8, rule.Id.Length)];
        return $"{act} {host}";
    }
}
