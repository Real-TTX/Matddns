using Matddns.Models;

namespace Matddns.Services;

public class UpdaterService : BackgroundService
{
    private readonly ConfigService _config;
    private readonly LogService _log;
    private readonly PublicIpClient _publicIp;
    private readonly UnifiClient _unifi;
    private readonly DynDnsClient _dyndns;
    private readonly NetcupClient _netcup;
    private readonly SourceResolver _resolver;
    private readonly ReachabilityChecker _reach;

    public UpdaterService(
        ConfigService config,
        LogService log,
        PublicIpClient publicIp,
        UnifiClient unifi,
        DynDnsClient dyndns,
        NetcupClient netcup,
        SourceResolver resolver,
        ReachabilityChecker reach)
    {
        _config = config;
        _log = log;
        _publicIp = publicIp;
        _unifi = unifi;
        _dyndns = dyndns;
        _netcup = netcup;
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
                _config.Mutate(c =>
                {
                    var g = c.Sources.FirstOrDefault(x => x.Id == group.Id);
                    if (g == null) return;
                    if (g.Entries.Count == 0)
                        g.Entries.Add(new SourceEntry { Label = "Public IP" });
                    var entry = g.Entries[0];
                    entry.Label = "Public IP";
                    entry.CurrentIp = ip;
                    entry.LastChecked = DateTime.UtcNow;
                    entry.LastError = ip == null ? "no IP" : null;
                });
                if (ip != null)
                    _log.Log(LogLevel.Debug, $"src:{group.Name}", $"Public IP {ip}");
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

                _config.Mutate(c =>
                {
                    var g = c.Sources.FirstOrDefault(x => x.Id == group.Id);
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
                        // Display-Name aus Unifi übernehmen (Label folgt der Unifi-Konfiguration).
                        if (!string.IsNullOrWhiteSpace(wan.DisplayName))
                            match.Label = wan.DisplayName!;
                        else if (string.IsNullOrWhiteSpace(match.Label))
                            match.Label = wan.Name.ToUpperInvariant();
                        match.CurrentIp = wan.Ip;
                        match.LastChecked = DateTime.UtcNow;
                        match.LastError = wan.Up ? (string.IsNullOrEmpty(wan.Ip) ? "kein Lease" : null) : "down";
                    }
                });
                _log.Log(LogLevel.Debug, $"src:{group.Name}",
                    $"Unifi WANs: {string.Join(", ", wans.Select(w => $"{(string.IsNullOrEmpty(w.DisplayName) ? w.Name : w.DisplayName)} [{w.Name}]={(w.Up ? (w.Ip ?? "no-ip") : "down")}"))}");
            }
        }
    }

    private async Task RunRulesAsync(CancellationToken ct)
    {
        var cfg = _config.Current;
        var now = DateTime.UtcNow;
        foreach (var rule in cfg.Rules)
        {
            if (!rule.Enabled) continue;
            if (!RuleDue(rule, now)) continue;

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
        }
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
            var ip = resolved.Entry.CurrentIp;
            if (string.IsNullOrWhiteSpace(ip)) continue;

            // Failover-Validierung: Quelle nur akzeptieren, wenn sie den Erreichbarkeits-Check besteht.
            if (rule.Validation != RuleValidation.None)
            {
                var reachable = await _reach.CheckAsync(rule.Validation, ip!, rule.ValidationPort, ct);
                var how = rule.Validation == RuleValidation.TcpPort ? $"TCP:{rule.ValidationPort}" : "Ping";
                _log.Log(LogLevel.Debug, $"rule:{RuleDesc(rule, cfg)}",
                    $"check {resolved.Entry.Label} {ip} ({how}) -> {(reachable ? "ok" : "fail")}");
                if (!reachable) continue;
            }

            if (dentry.Type == DnsRecordType.CNAME)
            {
                if (i >= rule.CnameTargets.Count) continue;
                var target = rule.CnameTargets[i];
                if (string.IsNullOrWhiteSpace(target)) continue;
                chosenValue = target;
            }
            else
            {
                chosenValue = ip;
            }
            chosenSourceId = sid;
            chosenIndex = i;
            break;
        }

        if (chosenValue == null)
        {
            // nur beim Zustandswechsel loggen (sonst Spam bei OnChange alle 15s)
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
            (ok, msg) = await _netcup.UpdateRecordAsync(dgroup.Netcup, domain, host, recordType, chosenValue, ct);
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
            // erfolgreicher Schreibvorgang = Zeitpunkt des Setzens (passiert durch Skip-Logik nur bei Änderung)
            if (ok) r.LastChange = DateTime.UtcNow;
            r.LastValue = chosenValue;
            r.LastUsedSourceId = chosenSourceId;
        });
    }

    private static bool RuleDue(Rule rule, DateTime now)
    {
        if (rule.LastRun == null) return true;
        var age = (now - rule.LastRun.Value).TotalSeconds;
        if (rule.Trigger == RuleTrigger.Interval)
            return age >= rule.IntervalSeconds;
        // OnChange: jede Schleife auswerten (Änderungserkennung passiert in RunSingleRuleAsync);
        // nach einem Fehler kurz drosseln, damit die DNS-API bei Fehlkonfiguration nicht geflutet wird.
        var lastErr = rule.LastResult != null && rule.LastResult.StartsWith("err", StringComparison.OrdinalIgnoreCase);
        return !lastErr || age >= 30;
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
