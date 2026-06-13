using Matddns.Models;
using Matddns.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matddns.Pages.Rules;

public class DynamicModel : PageModel
{
    private readonly ConfigService _config;
    public DynamicModel(ConfigService config) => _config = config;

    public Rule? RuleItem { get; private set; }
    public bool IsNew => RuleItem == null;
    public List<SourceGroup> PushSources { get; private set; } = new();
    public List<DomainGroup> NetcupGroups { get; private set; } = new();

    [TempData] public string? Notice { get; set; }
    [TempData] public string? Error { get; set; }

    private void Load()
    {
        var cfg = _config.Current;
        PushSources = cfg.Sources.Where(s => s.Kind == SourceKind.Push).ToList();
        NetcupGroups = cfg.Domains.Where(d => d.Kind == DomainKind.Netcup).ToList();
    }

    public IActionResult OnGet(string? id)
    {
        Load();
        if (!string.IsNullOrEmpty(id))
        {
            RuleItem = _config.Read(c => c.Rules.FirstOrDefault(r => r.Id == id && r.Dynamic));
            if (RuleItem == null) { Error = "Dynamic rule not found"; return RedirectToPage("Index"); }
        }
        return Page();
    }

    public IActionResult OnPostCreate(string DynamicSourceId, string DomainGroupId, string DynamicZone, string? DynamicPrefix)
    {
        if (string.IsNullOrWhiteSpace(DynamicSourceId) || string.IsNullOrWhiteSpace(DomainGroupId) || string.IsNullOrWhiteSpace(DynamicZone))
        { Error = "Receiver, target zone and zone are required"; return RedirectToPage("Dynamic"); }

        var rule = new Rule
        {
            Dynamic = true,
            DynamicSourceId = DynamicSourceId,
            DomainGroupId = DomainGroupId,
            DynamicZone = DynamicZone.Trim().Trim('.'),
            DynamicPrefix = (DynamicPrefix ?? "").Trim().Trim('.'),
            OnChange = false,
            IntervalSeconds = 0
        };
        _config.Mutate(c => c.Rules.Add(rule));
        Notice = "Dynamic rule created";
        return RedirectToPage("Dynamic", new { id = rule.Id });
    }

    public IActionResult OnPostSave(string Id, bool Enabled, string? DynamicSourceId, string? DomainGroupId, string? DynamicZone, string? DynamicPrefix)
    {
        _config.Mutate(c =>
        {
            var r = c.Rules.FirstOrDefault(x => x.Id == Id && x.Dynamic);
            if (r == null) return;
            r.Enabled = Enabled;
            if (!string.IsNullOrWhiteSpace(DynamicSourceId)) r.DynamicSourceId = DynamicSourceId;
            if (!string.IsNullOrWhiteSpace(DomainGroupId)) r.DomainGroupId = DomainGroupId;
            r.DynamicZone = (DynamicZone ?? "").Trim().Trim('.');
            r.DynamicPrefix = (DynamicPrefix ?? "").Trim().Trim('.');
        });
        Notice = "Saved";
        return RedirectToPage("Dynamic", new { id = Id });
    }

    public IActionResult OnPostDelete(string Id)
    {
        _config.Mutate(c => c.Rules.RemoveAll(r => r.Id == Id && r.Dynamic));
        Notice = "Dynamic rule deleted";
        return RedirectToPage("Index");
    }
}
