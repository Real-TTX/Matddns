using Matddns.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matddns.Pages;

public class SystemModel : PageModel
{
    private readonly SettingsService _settings;
    private readonly LogService _log;
    public SystemModel(SettingsService settings, LogService log)
    {
        _settings = settings;
        _log = log;
    }

    public Services.LogLevel CurrentLevel { get; private set; }
    public int RetentionDays { get; private set; }
    public string CurrentTimeZone { get; private set; } = "";
    public bool AnonymousDashboard { get; private set; }
    public List<TimeZoneInfo> Zones { get; private set; } = new();
    public long LogFileBytes { get; private set; }

    [TempData] public string? Notice { get; set; }
    [TempData] public string? Error { get; set; }

    public void OnGet()
    {
        var s = _settings.Get();
        CurrentLevel = s.MinLogLevel;
        RetentionDays = s.LogRetentionDays;
        CurrentTimeZone = s.TimeZone;
        AnonymousDashboard = s.AnonymousDashboard;
        Zones = TimeZoneInfo.GetSystemTimeZones().OrderBy(z => z.Id, StringComparer.Ordinal).ToList();
        LogFileBytes = _log.LogFileBytes();
    }

    // The same checks and the same entry in the log as a change through the platform API (/api/configure).
    public IActionResult OnPost(Services.LogLevel MinLogLevel, int LogRetentionDays, string? TimeZone, bool AnonymousDashboard)
    {
        var problems = _settings.Change(new GeneralSettingsChange(MinLogLevel, LogRetentionDays, TimeZone ?? "", AnonymousDashboard), User.Identity?.Name ?? "?");
        if (problems.Count > 0) Error = string.Join(" ", problems.Values);
        else Notice = "Saved.";
        return RedirectToPage();
    }

    public IActionResult OnPostClear()
    {
        _log.Clear();
        _log.Log(Services.LogLevel.Info, "settings", $"log cleared by {User.Identity?.Name ?? "?"}");
        Notice = "Log cleared.";
        return RedirectToPage();
    }
}
