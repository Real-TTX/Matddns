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

    // Failover validation: a source is only accepted if it passes ALL enabled checks.
    public bool ValidatePing { get; set; }
    public bool ValidateTcp { get; set; }
    public int ValidationPort { get; set; } = 443;

    // Legacy single validation; migrated into ValidatePing/ValidateTcp on load, then unused.
    public RuleValidation Validation { get; set; } = RuleValidation.None;
    public string DomainGroupId { get; set; } = "";
    public string DomainEntryId { get; set; } = "";
    public List<string> SourceEntryIdsInOrder { get; set; } = new();
    public List<string> CnameTargets { get; set; } = new();

    /// <summary>Who owns this object (e.g. "mattiverse", set through the management API); null = created by hand. Shown as a badge in the UI.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ManagedBy { get; set; }

    /// <summary>
    /// Keeps the trigger settings consistent (shared by the UI and the management API): ping/TCP failover needs a
    /// periodic re-check (a recovered source keeps its IP), so validation forces a minimum interval, and at least
    /// one trigger always stays active.
    /// </summary>
    public void NormalizeTriggers()
    {
        if ((ValidatePing || ValidateTcp) && IntervalSeconds <= 0) IntervalSeconds = 60;
        if (!OnChange && IntervalSeconds <= 0) OnChange = true;
    }

    // Dynamic (pattern) rule: a DynDNS Server source pushes hostnames; matching records are created/updated
    // on demand in the target Netcup zone (DomainGroupId). DomainEntryId / SourceEntryIdsInOrder are unused,
    // and the updater skips it (it is push-driven, written by PushReceiver).
    public bool Dynamic { get; set; }
    public string DynamicSourceId { get; set; } = "";   // the DynDNS Server (push) source group id
    public string DynamicZone { get; set; } = "";        // Netcup zone, e.g. h5x.de
    public string DynamicPrefix { get; set; } = "";      // namespace prefix under the zone, e.g. "dynamic" (empty = whole zone)

    [System.Text.Json.Serialization.JsonIgnore]
    public string DynamicBaseFqdn => string.IsNullOrWhiteSpace(DynamicPrefix) ? DynamicZone : $"{DynamicPrefix.Trim('.')}.{DynamicZone}";
    public DateTime? LastRun { get; set; }
    public string? LastResult { get; set; }
    public string? LastUsedSourceId { get; set; }
    public string? LastValue { get; set; }
    public DateTime? LastChange { get; set; }            // when the value was last actually set
    public string? LastSourceSignature { get; set; }     // snapshot of source IPs, to detect changes for OnChange

    /// <summary>Failed writes in a row (0 after a success); sets the wait before the next try, see <c>UpdaterService.RetryDelay</c>. Not written while 0.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int FailCount { get; set; }
}
