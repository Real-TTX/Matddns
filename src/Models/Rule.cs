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
    public RuleTrigger Trigger { get; set; } = RuleTrigger.OnChange;
    public RuleValidation Validation { get; set; } = RuleValidation.None;
    public int ValidationPort { get; set; } = 443;
    public string DomainGroupId { get; set; } = "";
    public string DomainEntryId { get; set; } = "";
    public List<string> SourceEntryIdsInOrder { get; set; } = new();
    public List<string> CnameTargets { get; set; } = new();
    public int IntervalSeconds { get; set; } = 300;
    public DateTime? LastRun { get; set; }
    public string? LastResult { get; set; }
    public string? LastUsedSourceId { get; set; }
    public string? LastValue { get; set; }
    public DateTime? LastChange { get; set; }   // wann der Wert zuletzt tatsächlich gesetzt wurde
}
