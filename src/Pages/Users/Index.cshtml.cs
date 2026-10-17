using Matddns.Controls;
using Matddns.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matddns.Pages.Users;

public class IndexModel : PageModel
{
    private const int PageSize = 15;

    private readonly ConfigService _config;
    public IndexModel(ConfigService config) => _config = config;

    public ListView<string> List { get; private set; } = default!;
    [TempData] public string? Notice { get; set; }
    [TempData] public string? Error { get; set; }

    public void OnGet()
    {
        var all = _config.Read(c => c.Users.Select(u => u.Username).ToList());
        var q = ListQuery.Text(Request, "q");
        var descending = ListQuery.Descending(Request, defaultDescending: false);

        IEnumerable<string> query = all;
        if (q is not null) query = query.Where(name => name.Contains(q, StringComparison.OrdinalIgnoreCase));
        query = descending ? query.OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase) : query.OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

        var list = query.ToList();
        var toolbar = new ToolbarModel
        {
            Id = "users",
            Action = "/Users",
            Label = "Users",
            Search = q,
            SearchPlaceholder = "Search users",
            SearchField = true,
            SortOptions = [new("username", "Username")],
            Sort = "username",
            Descending = descending,
            DefaultSort = "username",
            Total = all.Count,
            Matching = list.Count,
            Noun = all.Count == 1 ? "user" : "users",
        };

        var (items, pager) = ListQuery.Slice(list, Request, PageSize, toolbar.UrlWithPage);
        List = new ListView<string> { Items = items, Toolbar = toolbar, Pager = pager };
    }
}
