using Matddns.Controls;
using Matddns.Models;
using Matddns.Services;
using Matddns.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matddns.Pages.Domains;

public class IndexModel : PageModel
{
    private const int PageSize = 15;
    private static readonly StringComparison Ic = StringComparison.OrdinalIgnoreCase;
    private static readonly string[] SortKeys = ["name", "provider", "records"];

    private readonly ConfigService _config;
    public IndexModel(ConfigService config) => _config = config;

    public ListView<DomainGroup> List { get; private set; } = default!;
    public List<Rule> DynamicRules { get; private set; } = new();
    [TempData] public string? Notice { get; set; }
    [TempData] public string? Error { get; set; }

    public void OnGet()
    {
        var cfg = _config.Current;
        DynamicRules = cfg.Rules.Where(r => r.Dynamic).ToList();
        var all = cfg.Domains;
        var q = ListQuery.Text(Request, "q");
        var provider = Enum.TryParse<DomainKind>(ListQuery.Text(Request, "provider"), out var kindValue) ? kindValue.ToString() : null;
        var sort = ListQuery.Text(Request, "sort") is { } s && SortKeys.Contains(s) ? s : "name";
        var descending = ListQuery.Descending(Request, defaultDescending: false);

        IEnumerable<DomainGroup> query = all;
        if (provider is not null) query = query.Where(g => g.Kind.ToString() == provider);
        if (q is not null)
        {
            query = query.Where(g => g.Name.Contains(q, Ic) || Labels.Domain(g.Kind).Contains(q, Ic)
                || g.Entries.Any(e => e.Hostname.Contains(q, Ic))
                || DynamicRules.Any(r => r.DomainGroupId == g.Id && r.DynamicBaseFqdn.Contains(q, Ic)));
        }

        query = sort switch
        {
            "provider" => Order(query, g => Labels.Domain(g.Kind), descending),
            "records" => Order(query, g => g.Entries.Count, descending),
            _ => Order(query, g => g.Name.ToLowerInvariant(), descending),
        };

        var list = query.ToList();
        var toolbar = new ToolbarModel
        {
            Id = "domains",
            Action = "/Domains",
            Label = "Domain groups",
            Search = q,
            SearchPlaceholder = "Search groups and records",
            SearchField = true,
            Filters = [new FilterField("provider", "Provider", Enum.GetValues<DomainKind>().Select(k => new FilterOption(k.ToString(), Labels.Domain(k))).OrderBy(o => o.Label).ToList(), provider)],
            SortOptions = [new("name", "Name"), new("provider", "Provider"), new("records", "Number of records")],
            Sort = sort,
            Descending = descending,
            DefaultSort = "name",
            Total = all.Count,
            Matching = list.Count,
            Noun = all.Count == 1 ? "group" : "groups",
        };

        var (items, pager) = ListQuery.Slice(list, Request, PageSize, toolbar.UrlWithPage);
        List = new ListView<DomainGroup> { Items = items, Toolbar = toolbar, Pager = pager };
    }

    private static IEnumerable<DomainGroup> Order<TKey>(IEnumerable<DomainGroup> source, Func<DomainGroup, TKey> key, bool descending)
        => descending ? source.OrderByDescending(key) : source.OrderBy(key);
}
