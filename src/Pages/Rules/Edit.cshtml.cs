using Matddns.Models;
using Matddns.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matddns.Pages.Rules;

public class EditModel : PageModel
{
    private readonly ConfigService _config;
    private readonly SourceResolver _resolver;
    public EditModel(ConfigService config, SourceResolver resolver)
    {
        _config = config;
        _resolver = resolver;
    }

    public Rule? RuleItem { get; private set; }
    public bool IsNew => RuleItem == null;

    public List<DomainGroup> AllDomains { get; private set; } = new();
    public List<SourceGroup> AllSources { get; private set; } = new();
    public Dictionary<string, SourceResolver.ResolvedEntry> SourceLookup { get; private set; } = new();

    [TempData] public string? Notice { get; set; }
    [TempData] public string? Error { get; set; }

    private void LoadLookups()
    {
        var cfg = _config.Current;
        AllDomains = cfg.Domains.ToList();
        AllSources = cfg.Sources.ToList();
        SourceLookup = _resolver.AllEntries(cfg).ToDictionary(r => r.Entry.Id);
    }

    public bool HasAnyRecord => AllDomains.SelectMany(d => d.Entries).Any();

    public DomainEntry? TargetEntry
    {
        get
        {
            if (RuleItem == null) return null;
            var dg = AllDomains.FirstOrDefault(d => d.Id == RuleItem.DomainGroupId);
            return dg?.Entries.FirstOrDefault(e => e.Id == RuleItem.DomainEntryId);
        }
    }

    public DomainGroup? TargetGroup =>
        RuleItem == null ? null : AllDomains.FirstOrDefault(d => d.Id == RuleItem.DomainGroupId);

    public IActionResult OnGet(string? id)
    {
        LoadLookups();
        if (!string.IsNullOrEmpty(id))
        {
            RuleItem = _config.Read(c => c.Rules.FirstOrDefault(r => r.Id == id));
            if (RuleItem == null) { Error = "Rule not found"; return RedirectToPage("Index"); }
        }
        return Page();
    }

    public IActionResult OnPostCreate(bool OnChange, int IntervalSeconds, string DomainEntryRef,
        bool ValidatePing, bool ValidateTcp, int ValidationPort, string[]? SourceEntryIdsInOrder)
    {
        if (string.IsNullOrWhiteSpace(DomainEntryRef)) { Error = "Domain required"; return RedirectToPage("Edit"); }
        var parts = DomainEntryRef.Split(':', 2);
        if (parts.Length != 2) { Error = "Invalid selection"; return RedirectToPage("Edit"); }

        var picked = (SourceEntryIdsInOrder ?? Array.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        var rule = new Rule
        {
            OnChange = OnChange,
            IntervalSeconds = IntervalSeconds <= 0 ? 0 : Math.Max(15, IntervalSeconds),
            ValidatePing = ValidatePing,
            ValidateTcp = ValidateTcp,
            ValidationPort = ValidationPort is < 1 or > 65535 ? 443 : ValidationPort,
            DomainGroupId = parts[0],
            DomainEntryId = parts[1],
            SourceEntryIdsInOrder = picked,
            CnameTargets = picked.Select(_ => "").ToList()
        };
        if (!rule.OnChange && rule.IntervalSeconds <= 0) rule.OnChange = true; // keep at least one trigger active
        _config.Mutate(c => c.Rules.Add(rule));
        Notice = picked.Count > 0 ? "Rule created" : "Rule created – add failover sources below";
        return RedirectToPage("Edit", new { id = rule.Id });
    }

    public IActionResult OnPostSave(string Id, bool OnChange, int IntervalSeconds, bool Enabled, string DomainEntryRef,
        bool ValidatePing, bool ValidateTcp, int ValidationPort)
    {
        var parts = (DomainEntryRef ?? "").Split(':', 2);
        _config.Mutate(c =>
        {
            var r = c.Rules.FirstOrDefault(x => x.Id == Id);
            if (r == null) return;
            r.OnChange = OnChange;
            r.IntervalSeconds = IntervalSeconds <= 0 ? 0 : Math.Max(15, IntervalSeconds);
            if (!r.OnChange && r.IntervalSeconds <= 0) r.OnChange = true; // keep at least one trigger active
            r.Enabled = Enabled;
            r.ValidatePing = ValidatePing;
            r.ValidateTcp = ValidateTcp;
            if (ValidationPort is >= 1 and <= 65535) r.ValidationPort = ValidationPort;
            if (parts.Length == 2) { r.DomainGroupId = parts[0]; r.DomainEntryId = parts[1]; }
        });
        Notice = "Saved";
        return RedirectToPage("Edit", new { id = Id });
    }

    public IActionResult OnPostSaveSources(string Id, string[]? SourceEntryIdsInOrder, string[]? CnameTargets)
    {
        _config.Mutate(c =>
        {
            var r = c.Rules.FirstOrDefault(x => x.Id == Id);
            if (r == null) return;
            r.SourceEntryIdsInOrder = (SourceEntryIdsInOrder ?? Array.Empty<string>()).ToList();
            r.CnameTargets = (CnameTargets ?? Array.Empty<string>()).ToList();
            while (r.CnameTargets.Count < r.SourceEntryIdsInOrder.Count) r.CnameTargets.Add("");
        });
        Notice = "Failover saved";
        return RedirectToPage("Edit", new { id = Id });
    }

    public IActionResult OnPostDelete(string Id)
    {
        _config.Mutate(c => c.Rules.RemoveAll(r => r.Id == Id));
        Notice = "Rule deleted";
        return RedirectToPage("Index");
    }

    public IActionResult OnPostAddSource(string Id, string SourceEntryId)
    {
        _config.Mutate(c =>
        {
            var r = c.Rules.FirstOrDefault(x => x.Id == Id);
            if (r == null || string.IsNullOrWhiteSpace(SourceEntryId)) return;
            if (!r.SourceEntryIdsInOrder.Contains(SourceEntryId))
            {
                r.SourceEntryIdsInOrder.Add(SourceEntryId);
                r.CnameTargets.Add("");
            }
        });
        Notice = "Source added";
        return RedirectToPage("Edit", new { id = Id });
    }

    public IActionResult OnPostRemoveSource(string Id, string SourceId)
    {
        _config.Mutate(c =>
        {
            var r = c.Rules.FirstOrDefault(x => x.Id == Id);
            if (r == null) return;
            var idx = r.SourceEntryIdsInOrder.IndexOf(SourceId);
            if (idx >= 0)
            {
                r.SourceEntryIdsInOrder.RemoveAt(idx);
                if (idx < r.CnameTargets.Count) r.CnameTargets.RemoveAt(idx);
            }
        });
        return RedirectToPage("Edit", new { id = Id });
    }

    public IActionResult OnPostMoveSource(string Id, string SourceId, int Dir)
    {
        _config.Mutate(c =>
        {
            var r = c.Rules.FirstOrDefault(x => x.Id == Id);
            if (r == null) return;
            var idx = r.SourceEntryIdsInOrder.IndexOf(SourceId);
            var to = idx + Dir;
            if (idx < 0 || to < 0 || to >= r.SourceEntryIdsInOrder.Count) return;
            (r.SourceEntryIdsInOrder[idx], r.SourceEntryIdsInOrder[to]) = (r.SourceEntryIdsInOrder[to], r.SourceEntryIdsInOrder[idx]);
            if (idx < r.CnameTargets.Count && to < r.CnameTargets.Count)
                (r.CnameTargets[idx], r.CnameTargets[to]) = (r.CnameTargets[to], r.CnameTargets[idx]);
        });
        return RedirectToPage("Edit", new { id = Id });
    }

    public IActionResult OnPostRunNow(string Id)
    {
        _config.Mutate(c =>
        {
            var r = c.Rules.FirstOrDefault(x => x.Id == Id);
            if (r != null)
            {
                r.LastRun = null;
                r.LastValue = null;
                r.LastUsedSourceId = null;
                r.LastResult = null;
            }
        });
        Notice = "Run forced";
        return RedirectToPage("Edit", new { id = Id });
    }
}
