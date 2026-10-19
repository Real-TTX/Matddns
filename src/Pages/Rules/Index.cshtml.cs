using Matddns.Controls;
using Matddns.Models;
using Matddns.Services;
using Matddns.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matddns.Pages.Rules;

public class IndexModel : PageModel
{
    private const int PageSize = 15;
    private static readonly StringComparison Ic = StringComparison.OrdinalIgnoreCase;
    private static readonly string[] SortKeys = ["record", "status", "changed"];

    private readonly ConfigService _config;
    private readonly SourceResolver _resolver;
    public IndexModel(ConfigService config, SourceResolver resolver)
    {
        _config = config;
        _resolver = resolver;
    }

    public ListView<Rule> List { get; private set; } = default!;
    public List<DomainGroup> AllDomains { get; private set; } = new();
    public List<SourceGroup> AllSources { get; private set; } = new();
    public Dictionary<string, SourceResolver.ResolvedEntry> SourceLookup { get; private set; } = new();

    [TempData] public string? Notice { get; set; }
    [TempData] public string? Error { get; set; }

    public void OnGet()
    {
        var cfg = _config.Current;
        AllDomains = cfg.Domains.ToList();
        AllSources = cfg.Sources.ToList();
        SourceLookup = _resolver.AllEntries(cfg).ToDictionary(r => r.Entry.Id);

        var all = cfg.Rules;
        var q = ListQuery.Text(Request, "q");
        var status = ListQuery.Text(Request, "status") is "ok" or "issue" or "pending" or "disabled" ? ListQuery.Text(Request, "status") : null;
        var sort = ListQuery.Text(Request, "sort") is { } s && SortKeys.Contains(s) ? s : "record";
        var descending = ListQuery.Descending(Request, defaultDescending: sort == "changed");

        IEnumerable<Rule> query = all;
        if (status is not null) query = query.Where(r => StatusKey(r) == status);
        if (q is not null) query = query.Where(r => SearchText(r).Contains(q, Ic));

        query = sort switch
        {
            "status" => Order(query, StatusKey, descending),
            "changed" => Order(query, r => r.LastChange ?? DateTime.MinValue, descending),
            _ => Order(query, r => RecordText(r).ToLowerInvariant(), descending),
        };

        var list = query.ToList();
        var toolbar = new ToolbarModel
        {
            Id = "rules",
            Action = "/Rules",
            Label = "Rules",
            Search = q,
            SearchPlaceholder = "Search rules",
            SearchField = true,
            Filters = [new FilterField("status", "Status", [new("ok", "OK"), new("issue", "Problem"), new("pending", "Pending"), new("disabled", "Disabled")], status)],
            SortOptions = [new("record", "Record"), new("status", "Status"), new("changed", "Last change")],
            Sort = sort,
            Descending = descending,
            DefaultSort = "record",
            DefaultDescending = false,
            Total = all.Count,
            Matching = list.Count,
            Noun = all.Count == 1 ? "rule" : "rules",
        };

        var (items, pager) = ListQuery.Slice(list, Request, PageSize, toolbar.UrlWithPage);
        List = new ListView<Rule> { Items = items, Toolbar = toolbar, Pager = pager };
    }

    /// <summary>The state of a rule as the list and its filter name it: disabled, ok, issue (an error or nothing to write) or pending.</summary>
    public static string StatusKey(Rule r)
    {
        if (!r.Enabled) return "disabled";
        if (r.Dynamic) return "ok";
        if (!string.IsNullOrEmpty(r.LastResult) && r.LastResult.StartsWith("ok", StringComparison.OrdinalIgnoreCase)) return "ok";
        if (!string.IsNullOrEmpty(r.LastResult) && (r.LastResult.StartsWith("err", StringComparison.OrdinalIgnoreCase) || r.LastResult is "no source" or "target missing")) return "issue";
        return "pending";
    }

    public DomainEntry? TargetEntry(Rule r)
        => AllDomains.FirstOrDefault(d => d.Id == r.DomainGroupId)?.Entries.FirstOrDefault(e => e.Id == r.DomainEntryId);

    public string SourceLabel(string sourceEntryId)
        => SourceLookup.TryGetValue(sourceEntryId, out var v) ? $"{v.Group.Name} · {v.Entry.Label}" : "(unknown source)";

    /// <summary>What a rule is called in the list: its record ("home.example.com (A)") or, for a dynamic rule, its namespace.</summary>
    public string RecordText(Rule r)
        => r.Dynamic ? "*." + r.DynamicBaseFqdn : (TargetEntry(r)?.Display ?? "(domain missing)");

    private string SearchText(Rule r)
    {
        var group = AllDomains.FirstOrDefault(d => d.Id == r.DomainGroupId)?.Name;
        var sources = r.Dynamic
            ? AllSources.FirstOrDefault(s => s.Id == r.DynamicSourceId)?.Name
            : string.Join(' ', r.SourceEntryIdsInOrder.Select(SourceLabel));
        return $"{RecordText(r)} {group} {sources} {r.LastValue} {StatusKey(r)}";
    }

    private static IEnumerable<Rule> Order<TKey>(IEnumerable<Rule> source, Func<Rule, TKey> key, bool descending)
        => descending ? source.OrderByDescending(key) : source.OrderBy(key);
}
