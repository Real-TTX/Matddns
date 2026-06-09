using Matddns.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matddns.Pages;

public class LogModel : PageModel
{
    private readonly LogService _log;
    public LogModel(LogService log) => _log = log;

    public IReadOnlyList<LogEntry> Entries { get; private set; } = Array.Empty<LogEntry>();

    // Mindest-Level-Filter für die Anzeige; "Debug" = alles Aufgezeichnete.
    [BindProperty(SupportsGet = true)] public string? Level { get; set; }
    public Services.LogLevel MinFilter { get; private set; } = Services.LogLevel.Debug;

    public void OnGet()
    {
        if (!Enum.TryParse<Services.LogLevel>(Level, out var min)) min = Services.LogLevel.Debug;
        MinFilter = min;
        Entries = _log.Recent(500, min);
    }
}
