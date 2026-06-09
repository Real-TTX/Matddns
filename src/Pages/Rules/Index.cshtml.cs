using Matddns.Models;
using Matddns.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matddns.Pages.Rules;

public class IndexModel : PageModel
{
    private readonly ConfigService _config;
    private readonly SourceResolver _resolver;
    public IndexModel(ConfigService config, SourceResolver resolver)
    {
        _config = config;
        _resolver = resolver;
    }

    public List<Rule> Rules { get; private set; } = new();
    public List<DomainGroup> AllDomains { get; private set; } = new();
    public Dictionary<string, SourceResolver.ResolvedEntry> SourceLookup { get; private set; } = new();

    [Microsoft.AspNetCore.Mvc.TempData] public string? Notice { get; set; }
    [Microsoft.AspNetCore.Mvc.TempData] public string? Error { get; set; }

    public void OnGet()
    {
        var cfg = _config.Current;
        Rules = cfg.Rules.ToList();
        AllDomains = cfg.Domains.ToList();
        SourceLookup = _resolver.AllEntries(cfg).ToDictionary(r => r.Entry.Id);
    }

    public DomainEntry? TargetEntry(Rule r)
    {
        var dg = AllDomains.FirstOrDefault(d => d.Id == r.DomainGroupId);
        return dg?.Entries.FirstOrDefault(e => e.Id == r.DomainEntryId);
    }

    public string SourceLabel(string sourceEntryId) =>
        SourceLookup.TryGetValue(sourceEntryId, out var v) ? v.Entry.Label : "?";
}
