using Matddns.Controls;
using Matddns.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matddns.Pages;

public class LogModel : PageModel
{
    private const int PageSize = 50;
    private static readonly StringComparison Ic = StringComparison.OrdinalIgnoreCase;

    private readonly LogService _log;
    public LogModel(LogService log) => _log = log;

    public ListView<LogEntry> List { get; private set; } = default!;

    public void OnGet()
    {
        // "level" is a minimum: Warn shows warnings and errors. Without it everything that was recorded is shown.
        var level = Enum.TryParse<Services.LogLevel>(ListQuery.Text(Request, "level"), out var min) && min != Services.LogLevel.Debug ? min : (Services.LogLevel?)null;
        var q = ListQuery.Text(Request, "q");
        var descending = ListQuery.Descending(Request, defaultDescending: true); // newest first

        var all = _log.Recent(1000);
        IEnumerable<LogEntry> query = level is { } m ? all.Where(e => e.Level >= m) : all;
        if (q is not null) query = query.Where(e => e.Message.Contains(q, Ic) || e.Source.Contains(q, Ic));
        query = descending ? query.OrderByDescending(e => e.Timestamp) : query.OrderBy(e => e.Timestamp);

        var list = query.ToList();
        var toolbar = new ToolbarModel
        {
            Id = "log",
            Action = "/Log",
            Label = "Log",
            Search = q,
            SearchPlaceholder = "Search the log",
            SearchField = true,
            Filters =
            [
                new FilterField("level", "Level", [new("Info", "Info and above"), new("Update", "Update and above"), new("Warn", "Warn and above"), new("Error", "Error only")], level?.ToString()),
            ],
            SortOptions = [new("time", "Time")],
            Sort = "time",
            Descending = descending,
            DefaultSort = "time",
            DefaultDescending = true,
            Total = all.Count,
            Matching = list.Count,
            Noun = all.Count == 1 ? "entry" : "entries",
        };

        var (items, pager) = ListQuery.Slice(list, Request, PageSize, toolbar.UrlWithPage);
        List = new ListView<LogEntry> { Items = items, Toolbar = toolbar, Pager = pager };
    }
}
