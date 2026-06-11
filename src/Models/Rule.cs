namespace Matddns.Models;

public enum RuleTrigger
{
    OnChange,
    Interval
}

public enum RuleValidation
{
    None,
    Ping,
    TcpPort
}

public class Rule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;

    // Triggers are independent (both may be on):
    //  - OnChange: re-evaluate immediately when a source IP changes (event-driven).
    //  - IntervalSeconds: re-evaluate every N seconds (0 = off). Needed for ping/TCP failover,
    //    because a source can go DOWN without its IP changing — only a periodic re-check catches that.
    public bool OnChange { get; set; } = true;
    public int IntervalSeconds { get; set; } = 300;

    // Legacy single trigger; migrated into OnChange/IntervalSeconds on load, then unused.
    public RuleTrigger Trigger { get; set; } = RuleTrigger.OnChange;

    public RuleValidation Validation { get; set; } = RuleValidation.None;
    public int ValidationPort { get; set; } = 443;
    public string DomainGroupId { get; set; } = "";
    public string DomainEntryId { get; set; } = "";
    public List<string> SourceEntryIdsInOrder { get; set; } = new();
    public List<string> CnameTargets { get; set; } = new();
    public DateTime? LastRun { get; set; }
    public string? LastResult { get; set; }
    public string? LastUsedSourceId { get; set; }
    public string? LastValue { get; set; }
    public DateTime? LastChange { get; set; }            // when the value was last actually set
    public string? LastSourceSignature { get; set; }     // snapshot of source IPs, to detect changes for OnChange
}
