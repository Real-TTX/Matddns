using Matddns.Models;

namespace Matddns.Services;

/// <summary>Builds a health/state snapshot shared by the dashboard and the public /api endpoints.</summary>
public class StatusService
{
    private readonly ConfigService _config;
    private readonly LogService _log;
    private readonly HistoryService _history;
    public StatusService(ConfigService config, LogService log, HistoryService history)
    {
        _config = config;
        _log = log;
        _history = history;
    }

    // "on change", "every 300s", "on change + every 300s", or "manual"
    private static string ScheduleText(Rule r)
    {
        var parts = new List<string>();
        if (r.OnChange) parts.Add("on change");
        if (r.IntervalSeconds > 0) parts.Add($"every {r.IntervalSeconds}s");
        return parts.Count > 0 ? string.Join(" + ", parts) : "manual";
    }

    public StatusSnapshot Build(int recentChanges = 20)
    {
        var cfg = _config.Current;
        var now = DateTime.UtcNow;

        var sources = cfg.Sources.Select(s => new SourceStatus
        {
            Name = s.Name,
            Kind = s.Kind.ToString(),
            IntervalSeconds = s.IntervalSeconds,
            LastChecked = s.Entries.Where(e => e.LastChecked != null)
                                   .Select(e => (DateTime?)e.LastChecked!.Value)
                                   .DefaultIfEmpty(null).Max(),
            Entries = s.Entries.Select(e => new EntryStatus
            {
                Label = e.Label,
                Interface = e.InterfaceName,
                Ip = e.CurrentIp,
                Ipv6 = e.CurrentIpv6,
                LastChecked = e.LastChecked,
                Error = e.LastError
            }).ToList()
        }).ToList();

        var rules = cfg.Rules.Where(r => !r.Dynamic).Select(r =>
        {
            var dg = cfg.Domains.FirstOrDefault(d => d.Id == r.DomainGroupId);
            var de = dg?.Entries.FirstOrDefault(e => e.Id == r.DomainEntryId);
            var resultOk = r.LastResult != null && r.LastResult.StartsWith("ok", StringComparison.OrdinalIgnoreCase);
            var issue = r.Enabled && (
                (r.LastResult != null && r.LastResult.StartsWith("err", StringComparison.OrdinalIgnoreCase))
                || r.LastResult is "no source" or "target missing");
            var failover = r.SourceEntryIdsInOrder.Select(sid =>
            {
                var grp = cfg.Sources.FirstOrDefault(s => s.Entries.Any(e => e.Id == sid));
                var ent = grp?.Entries.FirstOrDefault(e => e.Id == sid);
                return new RuleSourceItem
                {
                    Name = ent != null ? $"{grp!.Name} · {ent.Label}" : "(unknown source)",
                    Ip = ent?.CurrentIp,
                    Ipv6 = ent?.CurrentIpv6,
                    Active = r.LastUsedSourceId == sid,
                    Missing = ent == null
                };
            }).ToList();

            return new RuleStatus
            {
                Target = de?.Hostname ?? "(missing)",
                Type = (de?.Type ?? DnsRecordType.A).ToString(),
                Group = dg?.Name,
                Enabled = r.Enabled,
                Trigger = ScheduleText(r),
                LastResult = r.LastResult,
                LastValue = r.LastValue,
                LastChange = r.LastChange,
                LastRun = r.LastRun,
                Ok = resultOk,
                Issue = issue,
                Sources = failover
            };
        }).ToList();

        var allEntries = sources.SelectMany(s => s.Entries).ToList();
        var sourceErrors = allEntries.Count(e => e.Error != null);
        var ruleIssues = rules.Count(r => r.Issue);

        var updates = _history.All();
        var errors24h = _log.EntriesOfLevel(LogLevel.Error, 1000).Count(e => e.Timestamp >= now.AddHours(-24));

        return new StatusSnapshot
        {
            Ok = sourceErrors == 0 && ruleIssues == 0,
            Time = now,
            ErrorsLast24h = errors24h,
            Sources = new CountSummary
            {
                Total = cfg.Sources.Count,
                Entries = allEntries.Count,
                Ok = allEntries.Count(e => e.Error == null && (!string.IsNullOrEmpty(e.Ip) || !string.IsNullOrEmpty(e.Ipv6))),
                Errors = sourceErrors
            },
            Rules = new RuleCountSummary
            {
                Total = cfg.Rules.Count(r => !r.Dynamic),
                Ok = rules.Count(r => r.Ok),
                Issues = ruleIssues,
                Disabled = cfg.Rules.Count(r => !r.Dynamic && !r.Enabled)
            },
            IpChanges = new IpChangeStats
            {
                Total = updates.Count,
                Last24h = updates.Count(e => e.Time >= now.AddHours(-24)),
                Last7d = updates.Count(e => e.Time >= now.AddDays(-7)),
                LastChange = updates.Count > 0 ? updates[0].Time : null,
                Recent = updates.Take(recentChanges).Select(e => new IpChangeItem
                {
                    Time = e.Time,
                    Target = $"{e.Type} {e.Record}",
                    Detail = Describe(e)
                }).ToList()
            },
            SourceList = sources,
            RuleList = rules
        };
    }

    /// <summary>"203.0.113.7, was 203.0.113.6, from Office · WAN1": one change in words.</summary>
    public static string Describe(IpChange c)
        => c.Value + (c.Previous != null ? $", was {c.Previous}" : "") + (c.Source != null ? $", from {c.Source}" : "");

    /// <summary>The values of the platform overview (see <see cref="OverviewStatus"/>).</summary>
    public OverviewStatus Overview()
    {
        var cfg = _config.Current;
        var records = cfg.Rules.Where(r => !r.Dynamic && r.Enabled).ToList();
        var entries = cfg.Sources.SelectMany(g => g.Entries.Select(e => (Group: g, Entry: e))).ToList();

        // the failover winner of most records; before the first rule run the first source that has an IP
        var activeId = records.Where(r => r.LastUsedSourceId != null)
            .GroupBy(r => r.LastUsedSourceId!)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();
        var active = entries.FirstOrDefault(x => x.Entry.Id == activeId);
        if (active.Entry == null)
            active = entries.FirstOrDefault(x => !string.IsNullOrEmpty(x.Entry.CurrentIp) || !string.IsNullOrEmpty(x.Entry.CurrentIpv6));

        var ipv4 = Has(active.Entry?.CurrentIp) ? active.Entry!.CurrentIp : entries.Select(x => x.Entry.CurrentIp).FirstOrDefault(Has);
        var ipv6 = Has(active.Entry?.CurrentIpv6) ? active.Entry!.CurrentIpv6 : entries.Select(x => x.Entry.CurrentIpv6).FirstOrDefault(Has);

        return new OverviewStatus(
            ipv4,
            ipv6,
            active.Entry == null ? null : $"{active.Group.Name} · {active.Entry.Label}",
            records.Count(r => r.LastUsedSourceId != null && r.SourceEntryIdsInOrder.Count > 0 && r.LastUsedSourceId != r.SourceEntryIdsInOrder[0]),
            records.Count,
            records.Count(r => r.LastResult != null && r.LastResult.StartsWith("ok", StringComparison.OrdinalIgnoreCase)),
            cfg.Rules.Max(r => r.LastChange),
            _log.EntriesOfLevel(LogLevel.Error, 1).FirstOrDefault());

        static bool Has(string? s) => !string.IsNullOrEmpty(s);
    }
}

/// <summary>
/// The few values of the overview of the Mat platform contract (the tiles of <c>/api/state</c>): the IPs that are written, where they come from,
/// how many records are in failover and in sync, when a record was last written and the last error.
/// </summary>
/// <param name="Ipv4">The IPv4 of the active source (else of the first source that has one).</param>
/// <param name="Ipv6">The IPv6 of the active source (else of the first source that has one).</param>
/// <param name="ActiveSource">"Source · entry" that most enabled records are written from now; without a rule run, the first source with an IP.</param>
/// <param name="Failovers">Enabled records written from a source other than their first choice.</param>
/// <param name="Records">Enabled records (non-dynamic rules).</param>
/// <param name="RecordsInSync">Of those, the ones whose last write succeeded.</param>
/// <param name="LastUpdate">When a record was last written successfully.</param>
/// <param name="LastError">The newest entry of the level Error in the log, or null.</param>
public sealed record OverviewStatus(
    string? Ipv4, string? Ipv6, string? ActiveSource, int Failovers, int Records, int RecordsInSync, DateTime? LastUpdate, LogEntry? LastError);

public class StatusSnapshot
{
    public bool Ok { get; set; }
    public DateTime Time { get; set; }
    public int ErrorsLast24h { get; set; }
    public CountSummary Sources { get; set; } = new();
    public RuleCountSummary Rules { get; set; } = new();
    public IpChangeStats IpChanges { get; set; } = new();
    public List<SourceStatus> SourceList { get; set; } = new();
    public List<RuleStatus> RuleList { get; set; } = new();
}

public class CountSummary
{
    public int Total { get; set; }
    public int Entries { get; set; }
    public int Ok { get; set; }
    public int Errors { get; set; }
}

public class RuleCountSummary
{
    public int Total { get; set; }
    public int Ok { get; set; }
    public int Issues { get; set; }
    public int Disabled { get; set; }
}

public class IpChangeStats
{
    public int Total { get; set; }
    public int Last24h { get; set; }
    public int Last7d { get; set; }
    public DateTime? LastChange { get; set; }
    public List<IpChangeItem> Recent { get; set; } = new();
}

public class IpChangeItem
{
    public DateTime Time { get; set; }
    public string Target { get; set; } = "";
    public string Detail { get; set; } = "";
}

public class SourceStatus
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public int IntervalSeconds { get; set; }
    public DateTime? LastChecked { get; set; }
    public List<EntryStatus> Entries { get; set; } = new();
}

public class EntryStatus
{
    public string Label { get; set; } = "";
    public string? Interface { get; set; }
    public string? Ip { get; set; }
    public string? Ipv6 { get; set; }
    public DateTime? LastChecked { get; set; }
    public string? Error { get; set; }
}

public class RuleStatus
{
    public string Target { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Group { get; set; }
    public bool Enabled { get; set; }
    public string Trigger { get; set; } = "";
    public string? LastResult { get; set; }
    public string? LastValue { get; set; }
    public DateTime? LastChange { get; set; }
    public DateTime? LastRun { get; set; }
    public bool Ok { get; set; }
    public bool Issue { get; set; }
    public List<RuleSourceItem> Sources { get; set; } = new();
}

public class RuleSourceItem
{
    public string Name { get; set; } = "";   // "SourceGroup · EntryLabel"
    public string? Ip { get; set; }
    public string? Ipv6 { get; set; }
    public bool Active { get; set; }          // the source currently written (failover winner)
    public bool Missing { get; set; }         // the source/entry no longer exists

    /// <summary>The address that counts for a record of <paramref name="type"/> (A, AAAA, CNAME).</summary>
    public string? AddressFor(string type) => RecordAddress.For(type, Ip, Ipv6);
}

/// <summary>The address of a source that matters for a record type: IPv4 for A, IPv6 for AAAA, either for CNAME.</summary>
public static class RecordAddress
{
    public static string? For(string type, string? ipv4, string? ipv6) => type switch
    {
        "A" => Has(ipv4) ? ipv4 : null,
        "AAAA" => Has(ipv6) ? ipv6 : null,
        _ => Has(ipv4) ? ipv4 : Has(ipv6) ? ipv6 : null,
    };

    private static bool Has(string? s) => !string.IsNullOrEmpty(s);
}
