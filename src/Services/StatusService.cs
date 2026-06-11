using Matddns.Models;

namespace Matddns.Services;

/// <summary>Builds a health/state snapshot shared by the dashboard and the public /api endpoints.</summary>
public class StatusService
{
    private readonly ConfigService _config;
    private readonly LogService _log;
    public StatusService(ConfigService config, LogService log)
    {
        _config = config;
        _log = log;
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

        var rules = cfg.Rules.Select(r =>
        {
            var dg = cfg.Domains.FirstOrDefault(d => d.Id == r.DomainGroupId);
            var de = dg?.Entries.FirstOrDefault(e => e.Id == r.DomainEntryId);
            var resultOk = r.LastResult != null && r.LastResult.StartsWith("ok", StringComparison.OrdinalIgnoreCase);
            var issue = r.Enabled && (
                (r.LastResult != null && r.LastResult.StartsWith("err", StringComparison.OrdinalIgnoreCase))
                || r.LastResult is "no source" or "target missing");
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
                Issue = issue
            };
        }).ToList();

        var allEntries = sources.SelectMany(s => s.Entries).ToList();
        var sourceErrors = allEntries.Count(e => e.Error != null);
        var ruleIssues = rules.Count(r => r.Issue);

        var updates = _log.EntriesOfLevel(LogLevel.Update, 1000);
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
                Total = cfg.Rules.Count,
                Ok = rules.Count(r => r.Ok),
                Issues = ruleIssues,
                Disabled = cfg.Rules.Count(r => !r.Enabled)
            },
            IpChanges = new IpChangeStats
            {
                Total = updates.Count,
                Last24h = updates.Count(e => e.Timestamp >= now.AddHours(-24)),
                Last7d = updates.Count(e => e.Timestamp >= now.AddDays(-7)),
                LastChange = updates.Count > 0 ? updates[0].Timestamp : null,
                Recent = updates.Take(recentChanges).Select(e => new IpChangeItem
                {
                    Time = e.Timestamp,
                    Target = e.Source.StartsWith("rule:", StringComparison.Ordinal) ? e.Source[5..] : e.Source,
                    Detail = e.Message
                }).ToList()
            },
            SourceList = sources,
            RuleList = rules
        };
    }
}

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
}
