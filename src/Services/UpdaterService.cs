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
    private readonly MatddnsClient _matddns;
    private readonly RecordWriter _writer;
    private readonly SourceResolver _resolver;
    private readonly ReachabilityChecker _reach;
    private readonly HistoryService _history;
    private readonly NotificationService _notify;

    // one evaluation pass at a time: the periodic loop and an on-demand run (management API) never overlap
    private readonly SemaphoreSlim _pass = new(1, 1);

    public UpdaterService(
        ConfigService config,
        LogService log,
        PublicIpClient publicIp,
        DnsLookupClient dns,
        FritzboxClient fritz,
        UnifiClient unifi,
        UnifiCloudClient unifiCloud,
        MatddnsClient matddns,
        RecordWriter writer,
        SourceResolver resolver,
        ReachabilityChecker reach,
        HistoryService history,
        NotificationService notify)
    {
        _config = config;
        _log = log;
        _publicIp = publicIp;
        _dns = dns;
        _fritz = fritz;
        _unifi = unifi;
        _unifiCloud = unifiCloud;
        _matddns = matddns;
        _writer = writer;
        _resolver = resolver;
        _reach = reach;
        _history = history;
        _notify = notify;
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
                await _pass.WaitAsync(stoppingToken);
                try
                {
                    await RefreshSourcesAsync(stoppingToken);
                    await RunRulesAsync(stoppingToken);
                }
                finally { _pass.Release(); }
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

    /// <summary>
    /// Runs one evaluation pass now instead of waiting for the next 15-second cycle (management API <c>POST /api/v1/run</c>):
    /// refreshes the sources that are due and evaluates every enabled rule, ignoring the error back-off. A rule whose
    /// chosen value did not change is not written again, so this never causes needless DNS updates.
    /// </summary>
    public async Task RunNowAsync(CancellationToken ct)
    {
        await _pass.WaitAsync(ct);
        try
        {
            await RefreshSourcesAsync(ct);
            await RunRulesAsync(ct, force: true);
        }
        finally { _pass.Release(); }
    }
    // ---- sources ----

    private async Task RefreshSourcesAsync(CancellationToken ct)
    {
        var cfg = _config.Current;
        var now = DateTime.UtcNow;

        foreach (var group in cfg.Sources)
        {
            var interval = Math.Max(group.IntervalSeconds, 15); // never poll a network source more often than the 15s loop
            var due = group.Entries.All(e => e.LastChecked == null) ||
                      group.Entries.Any(e => e.LastChecked == null || (now - e.LastChecked.Value).TotalSeconds >= interval);
            if (!due) continue;

            // Push sources are not polled: their IP arrives via PushReceiver.
            switch (group.Kind)
            {
                case SourceKind.PublicIp: await RefreshPublicIpAsync(group, ct); break;
                case SourceKind.Unifi when group.Unifi is { } unifi: await RefreshUnifiAsync(group, unifi, ct); break;
                case SourceKind.UnifiCloud when group.UnifiCloud is { } cloud: await RefreshUnifiCloudAsync(group, cloud, ct); break;
                case SourceKind.Matddns when group.Matddns is { } peer: await RefreshMatddnsPeerAsync(group, peer, ct); break;
                case SourceKind.Static: RefreshStatic(group); break;
                case SourceKind.Dns: await RefreshDnsAsync(group, ct); break;
                case SourceKind.Fritzbox when group.Fritzbox is { } fritz: await RefreshFritzboxAsync(group, fritz, ct); break;
            }
        }
    }

    private void MutateGroup(string groupId, Action<SourceGroup> change) =>
        _config.Mutate(c =>
        {
            var group = c.Sources.FirstOrDefault(x => x.Id == groupId);
            if (group != null) change(group);
        });

    private void MarkEntriesFailed(string groupId, string error) =>
        MutateGroup(groupId, g =>
        {
            foreach (var entry in g.Entries)
            {
                entry.LastChecked = DateTime.UtcNow;
                entry.LastError = error;
            }
        });

    // Sources with a single address pair (public IP, static, DNS, FRITZ!Box) keep it in their first entry.
    private void WriteSingleEntry(string groupId, string label, string? ipv4, string? ipv6, string? error, bool updateAddresses = true) =>
        MutateGroup(groupId, g =>
        {
            if (g.Entries.Count == 0) g.Entries.Add(new SourceEntry());
            var entry = g.Entries[0];
            entry.Label = label;
            if (updateAddresses)
            {
                entry.CurrentIp = ipv4;
                entry.CurrentIpv6 = ipv6;
            }
            entry.LastChecked = DateTime.UtcNow;
            entry.LastError = error;
        });

    private async Task RefreshPublicIpAsync(SourceGroup group, CancellationToken ct)
    {
        var ip = await _publicIp.GetPublicIpAsync(ct);
        var ipv6 = await _publicIp.GetPublicIpv6Async(ct);
        WriteSingleEntry(group.Id, "Public IP", ip, ipv6, (ip == null && ipv6 == null) ? "no IP" : null);
        if (ip != null || ipv6 != null)
            _log.Log(LogLevel.Debug, $"src:{group.Name}", $"Public IP v4={ip ?? "-"} v6={ipv6 ?? "-"}");
        else
            _log.Log(LogLevel.Warn, $"src:{group.Name}", "could not resolve public IP");
    }

    private async Task RefreshUnifiAsync(SourceGroup group, UnifiSettings settings, CancellationToken ct)
    {
        List<UnifiClient.WanInfo> wans;
        try { wans = await _unifi.GetWansAsync(settings, ct); }
        catch (Exception ex)
        {
            _log.Log(LogLevel.Error, $"src:{group.Name}", $"UniFi: {ex.Message}");
            MarkEntriesFailed(group.Id, ex.Message);
            return;
        }

        WriteWanEntries(group.Id, wans);
        _log.Log(LogLevel.Debug, $"src:{group.Name}",
            $"UniFi WANs: {string.Join(", ", wans.Select(w => $"{(string.IsNullOrEmpty(w.DisplayName) ? w.Name : w.DisplayName)} [{w.Name}]={(w.Up ? (w.Ip ?? "no-ip") : "down")}"))}");
    }

    // Upsert one source entry per WAN (matched by interface group name).
    private void WriteWanEntries(string groupId, List<UnifiClient.WanInfo> wans) =>
        MutateGroup(groupId, g =>
        {
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

    private async Task RefreshUnifiCloudAsync(SourceGroup group, UnifiCloudSettings settings, CancellationToken ct)
    {
        if (group.Entries.Count == 0) return; // no gateways selected yet — nothing to poll

        List<UnifiCloudClient.HostInfo> hosts;
        try { hosts = await _unifiCloud.ListHostsAsync(settings.ApiKey, ct); }
        catch (Exception ex)
        {
            _log.Log(LogLevel.Error, $"src:{group.Name}", $"UniFi Cloud: {ex.Message}");
            MarkEntriesFailed(group.Id, ex.Message);
            return;
        }

        var byId = hosts.ToDictionary(h => h.Id, h => h, StringComparer.Ordinal);
        MutateGroup(group.Id, g =>
        {
            foreach (var e in g.Entries)
            {
                e.LastChecked = DateTime.UtcNow;
                if (e.InterfaceName != null && byId.TryGetValue(e.InterfaceName, out var host))
                {
                    // keep the user's alias (Label) — only refresh the IPs
                    e.CurrentIp = host.Ipv4;
                    e.CurrentIpv6 = host.Ipv6;
                    e.LastError = (host.Ipv4 == null && host.Ipv6 == null) ? "no public IP" : null;
                }
                else
                {
                    e.LastError = "gateway not found";
                }
            }
        });
        _log.Log(LogLevel.Debug, $"src:{group.Name}",
            $"UniFi Cloud: {string.Join(", ", group.Entries.Select(e => $"{e.Label}={(byId.TryGetValue(e.InterfaceName ?? "", out var h) ? (h.Ipv4 ?? h.Ipv6 ?? "no-ip") : "missing")}"))}");
    }

    private async Task RefreshMatddnsPeerAsync(SourceGroup group, MatddnsLinkSettings peer, CancellationToken ct)
    {
        List<MatddnsClient.RemoteEntry> remote;
        try { remote = await _matddns.PullAsync(peer, ct); }
        catch (Exception ex)
        {
            _log.Log(LogLevel.Error, $"src:{group.Name}", $"Matddns peer: {ex.Message}");
            MarkEntriesFailed(group.Id, ex.Message);
            return;
        }

        MutateGroup(group.Id, g =>
        {
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

    private void RefreshStatic(SourceGroup group) =>
        MutateGroup(group.Id, g =>
        {
            var ip = g.Static?.Ip;
            var ipv6 = g.Static?.Ipv6;
            if (g.Entries.Count == 0)
                g.Entries.Add(new SourceEntry { Label = "Static IP" });
            var entry = g.Entries[0];
            entry.Label = "Static IP";
            entry.CurrentIp = string.IsNullOrWhiteSpace(ip) ? null : ip.Trim();
            entry.CurrentIpv6 = string.IsNullOrWhiteSpace(ipv6) ? null : ipv6.Trim();
            entry.LastChecked = DateTime.UtcNow;
            entry.LastError = (string.IsNullOrWhiteSpace(ip) && string.IsNullOrWhiteSpace(ipv6)) ? "no IP configured" : null;
        });

    private async Task RefreshDnsAsync(SourceGroup group, CancellationToken ct)
    {
        var host = group.Dns?.Hostname ?? "";
        var (v4, v6) = await _dns.ResolveAsync(host, ct);
        var error = (v4 == null && v6 == null)
            ? (string.IsNullOrWhiteSpace(host) ? "no hostname configured" : "could not resolve")
            : null;
        WriteSingleEntry(group.Id, string.IsNullOrWhiteSpace(host) ? "DNS lookup" : host, v4, v6, error);
        if (v4 != null || v6 != null)
            _log.Log(LogLevel.Debug, $"src:{group.Name}", $"DNS {host} v4={v4 ?? "-"} v6={v6 ?? "-"}");
        else
            _log.Log(LogLevel.Warn, $"src:{group.Name}", $"could not resolve {host}");
    }

    private async Task RefreshFritzboxAsync(SourceGroup group, FritzboxSettings settings, CancellationToken ct)
    {
        string? v4 = null, v6 = null, error = null;
        try { var wan = await _fritz.GetWanAsync(settings, ct); v4 = wan.Ipv4; v6 = wan.Ipv6; }
        catch (Exception ex) { error = ex.Message; }

        // on a failed read keep the last known addresses
        WriteSingleEntry(group.Id, "FRITZ!Box WAN", v4, v6, error ?? ((v4 == null && v6 == null) ? "no IP" : null), updateAddresses: error == null);
        if (error == null) _log.Log(LogLevel.Debug, $"src:{group.Name}", $"FRITZ!Box v4={v4 ?? "-"} v6={v6 ?? "-"}");
        else _log.Log(LogLevel.Error, $"src:{group.Name}", $"FRITZ!Box: {error}");
    }

    // ---- rules ----

    private void MutateRule(string ruleId, Action<Rule> change) =>
        _config.Mutate(c =>
        {
            var rule = c.Rules.FirstOrDefault(x => x.Id == ruleId);
            if (rule != null) change(rule);
        });

    private async Task RunRulesAsync(CancellationToken ct, bool force = false)
    {
        var cfg = _config.Current;
        var now = DateTime.UtcNow;
        foreach (var rule in cfg.Rules)
        {
            if (!rule.Enabled) continue;
            if (rule.Dynamic) continue; // push-driven; written by PushReceiver, not polled here
            var signature = SourceSignature(rule, cfg);
            if (!force && !RuleDue(rule, now, signature)) continue;

            try { await RunSingleRuleAsync(rule, cfg, signature, ct); }
            catch (Exception ex)
            {
                // leave LastSourceSignature untouched so the failed evaluation is retried next cycle
                _log.Log(LogLevel.Error, $"rule:{RuleDesc(rule, cfg)}", ex.Message);
                MutateRule(rule.Id, r => { r.LastRun = DateTime.UtcNow; r.LastResult = "error: " + ex.Message; r.FailCount++; });
                if (rule.FailCount == 0)
                    _notify.Notify(new Notification(NotificationKind.Failure, $"{RuleDesc(rule, cfg)} could not be updated", $"{RuleDesc(rule, cfg)}: {ex.Message}. Matddns keeps trying."));
            }
        }
    }

    // Concatenated current IPs of the rule's sources; changes when any source IP changes.
    internal static string SourceSignature(Rule rule, AppConfig cfg)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var sid in rule.SourceEntryIdsInOrder)
        {
            var e = cfg.Sources.SelectMany(s => s.Entries).FirstOrDefault(x => x.Id == sid);
            sb.Append(e?.CurrentIp ?? "-").Append('/').Append(e?.CurrentIpv6 ?? "-").Append(';');
        }
        return sb.ToString();
    }

    private async Task RunSingleRuleAsync(Rule rule, AppConfig cfg, string signature, CancellationToken ct)
    {
        var dgroup = cfg.Domains.FirstOrDefault(d => d.Id == rule.DomainGroupId);
        var dentry = dgroup?.Entries.FirstOrDefault(e => e.Id == rule.DomainEntryId);
        if (dgroup == null || dentry == null)
        {
            if (rule.LastResult != "target missing")
            {
                _log.Log(LogLevel.Warn, $"rule:{RuleDesc(rule, cfg)}", "domain/entry not found");
                MutateRule(rule.Id, r => { r.LastRun = DateTime.UtcNow; r.LastResult = "target missing"; r.LastSourceSignature = signature; });
            }
            return;
        }

        var chosen = await ChooseSourceAsync(rule, cfg, dentry, ct);
        if (chosen == null)
        {
            // only log on state change (otherwise spam with OnChange every 15s)
            if (rule.LastResult != "no source")
            {
                _log.Log(LogLevel.Warn, $"rule:{RuleDesc(rule, cfg)}", "no reachable source");
                if (rule.LastRun != null) // not on the very first evaluation after a start, while the sources are still being read
                    _notify.Notify(new Notification(NotificationKind.Failure, $"{dentry.Hostname} ({dentry.Type}) has no source",
                        $"No source of the rule for {dentry.Hostname} ({dentry.Type}) has a usable address; the record keeps {rule.LastValue ?? "its value"}.",
                        dentry.Hostname, dentry.Type.ToString(), rule.LastValue));
            }
            MutateRule(rule.Id, r => { r.LastRun = DateTime.UtcNow; r.LastResult = "no source"; r.LastSourceSignature = signature; });
            return;
        }
        var (chosenValue, chosenSourceId, chosenIndex) = chosen.Value;

        if (rule.LastValue == chosenValue && rule.LastUsedSourceId == chosenSourceId &&
            rule.LastResult != null && rule.LastResult.StartsWith("ok", StringComparison.OrdinalIgnoreCase))
        {
            MutateRule(rule.Id, r => { r.LastRun = DateTime.UtcNow; r.LastSourceSignature = signature; });
            return;
        }

        var (ok, msg) = await _writer.WriteAsync(dgroup, dentry, chosenValue, ct);
        _log.Log(ok ? LogLevel.Update : LogLevel.Error, $"rule:{RuleDesc(rule, cfg)}",
            $"{dentry.Type} {dentry.Hostname} -> {chosenValue} (src#{chosenIndex}) : {msg}");

        MutateRule(rule.Id, r =>
        {
            r.LastRun = DateTime.UtcNow;
            r.LastResult = ok ? "ok: " + msg : "err: " + msg;
            // advance the signature only on success, so a failed write is retried next cycle instead of being masked
            if (ok)
            {
                r.LastChange = DateTime.UtcNow; // a successful write = the moment it was set (happens only on change due to the skip logic)
                r.FailCount = 0;
                r.LastSourceSignature = signature;
            }
            else r.FailCount++;
            r.LastValue = chosenValue;
            r.LastUsedSourceId = chosenSourceId;
        });

        var record = $"{dentry.Hostname} ({dentry.Type})";
        var source = _resolver.Find(cfg, chosenSourceId) is { } s ? $"{s.Group.Name} · {s.Entry.Label}" : null;
        var wasFailing = rule.LastResult is "no source" || (rule.LastResult?.StartsWith("err", StringComparison.OrdinalIgnoreCase) ?? false);
        if (ok)
        {
            if (_history.Record(rule.Id, dentry.Hostname, dentry.Type.ToString(), chosenValue, source) is { } change)
                _notify.Notify(new Notification(NotificationKind.Change, $"{record} is now {chosenValue}",
                    $"{record} is now {StatusService.Describe(change)}.", dentry.Hostname, dentry.Type.ToString(), chosenValue, change.Previous, source));
            if (wasFailing)
                _notify.Notify(new Notification(NotificationKind.Recovery, $"{record} works again",
                    $"{record} was written again: {chosenValue}.", dentry.Hostname, dentry.Type.ToString(), chosenValue, null, source));
        }
        else if (rule.FailCount == 0)
        {
            // once, at the first failure in a row; the back-off keeps trying and the recovery is told too
            _notify.Notify(new Notification(NotificationKind.Failure, $"{record} could not be updated",
                $"{record} could not be set to {chosenValue}: {msg}. Matddns keeps trying.", dentry.Hostname, dentry.Type.ToString(), chosenValue, null, source));
        }
    }

    // First source in failover order that has an address for the record type and passes all enabled reachability checks.
    private async Task<(string Value, string SourceId, int Index)?> ChooseSourceAsync(Rule rule, AppConfig cfg, DomainEntry target, CancellationToken ct)
    {
        for (var i = 0; i < rule.SourceEntryIdsInOrder.Count; i++)
        {
            var sourceId = rule.SourceEntryIdsInOrder[i];
            var resolved = _resolver.Find(cfg, sourceId);
            if (resolved == null) continue;

            // pick the address matching the record family (A=IPv4, AAAA=IPv6; CNAME uses any IP just for reachability)
            var familyIp = target.Type switch
            {
                DnsRecordType.AAAA => resolved.Entry.CurrentIpv6,
                DnsRecordType.A => resolved.Entry.CurrentIp,
                _ => resolved.Entry.CurrentIp ?? resolved.Entry.CurrentIpv6
            };
            if (string.IsNullOrWhiteSpace(familyIp)) continue; // source has no usable address for this record type

            var candidateValue = familyIp;
            if (target.Type == DnsRecordType.CNAME)
            {
                if (i >= rule.CnameTargets.Count || string.IsNullOrWhiteSpace(rule.CnameTargets[i])) continue;
                candidateValue = rule.CnameTargets[i];
            }

            if ((rule.ValidatePing || rule.ValidateTcp) && !await IsReachableAsync(rule, cfg, resolved.Entry.Label, familyIp, ct))
                continue;

            return (candidateValue, sourceId, i);
        }
        return null;
    }

    // A source is accepted only if it passes ALL enabled checks (ping and/or TCP).
    private async Task<bool> IsReachableAsync(Rule rule, AppConfig cfg, string sourceLabel, string ip, CancellationToken ct)
    {
        var reachable = (!rule.ValidatePing || await _reach.PingAsync(ip))
                        && (!rule.ValidateTcp || await _reach.TcpAsync(ip, rule.ValidationPort, ct));
        var how = string.Join("+", new[] { rule.ValidatePing ? "Ping" : null, rule.ValidateTcp ? $"TCP:{rule.ValidationPort}" : null }.Where(x => x != null));
        _log.Log(LogLevel.Debug, $"rule:{RuleDesc(rule, cfg)}", $"check {sourceLabel} {ip} ({how}) -> {(reachable ? "ok" : "fail")}");
        return reachable;
    }

    // Due when: never run yet, the periodic interval elapsed, or (OnChange) a source IP changed since the last evaluation.
    internal static bool RuleDue(Rule rule, DateTime now, string signature)
    {
        if (rule.LastRun == null) return true; // first evaluation after (re)start
        var age = (now - rule.LastRun.Value).TotalSeconds;

        // back off after an error, longer with every failure in a row, so a misconfiguration or an outage doesn't flood the DNS API
        var lastErr = rule.LastResult != null && rule.LastResult.StartsWith("err", StringComparison.OrdinalIgnoreCase);
        if (lastErr && age < RetryDelay(rule.FailCount).TotalSeconds) return false;

        // time-driven: periodic re-check — re-validates reachability, so a source going DOWN
        // (without its IP changing) still triggers failover.
        if (rule.IntervalSeconds > 0 && age >= rule.IntervalSeconds) return true;

        // event-driven: a source IP changed since the last evaluation.
        if (rule.OnChange && signature != (rule.LastSourceSignature ?? "")) return true;

        return false;
    }

    /// <summary>The wait after the <paramref name="failures"/>-th failed write in a row: 30 s, then doubled each time, at most 30 minutes.</summary>
    internal static TimeSpan RetryDelay(int failures)
    {
        var seconds = 30.0 * Math.Pow(2, Math.Clamp(failures, 1, 16) - 1);
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryDelaySeconds));
    }

    internal const int MaxRetryDelaySeconds = 1800;


    private static string RuleDesc(Rule rule, AppConfig cfg)
    {
        var dg = cfg.Domains.FirstOrDefault(d => d.Id == rule.DomainGroupId);
        var de = dg?.Entries.FirstOrDefault(e => e.Id == rule.DomainEntryId);
        var act = (de?.Type ?? DnsRecordType.A).ToString();
        var host = de?.Hostname ?? rule.Id[..Math.Min(8, rule.Id.Length)];
        return $"{act} {host}";
    }
}
