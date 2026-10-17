using Matddns.Controls;
using Matddns.Models;
using Matddns.Services;
using Matddns.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matddns.Pages.Sources;

public class IndexModel : PageModel
{
    private const int PageSize = 15;
    private static readonly StringComparison Ic = StringComparison.OrdinalIgnoreCase;
    private static readonly string[] SortKeys = ["name", "type", "status"];

    private readonly ConfigService _config;
    public IndexModel(ConfigService config) => _config = config;

    public ListView<SourceGroup> List { get; private set; } = default!;
    [TempData] public string? Notice { get; set; }
    [TempData] public string? Error { get; set; }

    public void OnGet()
    {
        var all = _config.Current.Sources;
        var q = ListQuery.Text(Request, "q");
        var kind = Enum.TryParse<SourceKind>(ListQuery.Text(Request, "type"), out var kindValue) ? kindValue.ToString() : null;
        var status = ListQuery.Text(Request, "status") is "ok" or "err" or "warn" ? ListQuery.Text(Request, "status") : null;
        var sort = ListQuery.Text(Request, "sort") is { } s && SortKeys.Contains(s) ? s : "name";
        var descending = ListQuery.Descending(Request, defaultDescending: false);

        IEnumerable<SourceGroup> query = all;
        if (kind is not null) query = query.Where(g => g.Kind.ToString() == kind);
        if (status is not null) query = query.Where(g => Labels.State(g) == status);
        if (q is not null)
        {
            query = query.Where(g => g.Name.Contains(q, Ic) || Labels.Source(g.Kind).Contains(q, Ic)
                || g.Entries.Any(e => (e.Label?.Contains(q, Ic) ?? false) || (e.CurrentIp?.Contains(q, Ic) ?? false) || (e.CurrentIpv6?.Contains(q, Ic) ?? false)));
        }

        query = sort switch
        {
            "type" => Order(query, g => Labels.Source(g.Kind), descending),
            "status" => Order(query, g => Labels.State(g), descending),
            _ => Order(query, g => g.Name.ToLowerInvariant(), descending),
        };

        var list = query.ToList();
        var toolbar = new ToolbarModel
        {
            Id = "sources",
            Action = "/Sources",
            Label = "Sources",
            Search = q,
            SearchPlaceholder = "Search sources and addresses",
            SearchField = true,
            Filters =
            [
                new FilterField("type", "Type", Enum.GetValues<SourceKind>().Select(k => new FilterOption(k.ToString(), Labels.Source(k))).OrderBy(o => o.Label).ToList(), kind),
                new FilterField("status", "Status", [new("ok", "OK"), new("err", "Error"), new("warn", "Pending")], status),
            ],
            SortOptions = [new("name", "Name"), new("type", "Type"), new("status", "Status")],
            Sort = sort,
            Descending = descending,
            DefaultSort = "name",
            Total = all.Count,
            Matching = list.Count,
            Noun = all.Count == 1 ? "source" : "sources",
        };

        var (items, pager) = ListQuery.Slice(list, Request, PageSize, toolbar.UrlWithPage);
        List = new ListView<SourceGroup> { Items = items, Toolbar = toolbar, Pager = pager };
    }

    private static IEnumerable<SourceGroup> Order<TKey>(IEnumerable<SourceGroup> source, Func<SourceGroup, TKey> key, bool descending)
        => descending ? source.OrderByDescending(key) : source.OrderBy(key);
}
