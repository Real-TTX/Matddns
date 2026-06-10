using Matddns.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matddns.Pages;

public class SystemModel : PageModel
{
    private readonly ConfigService _config;
    private readonly LogService _log;
    public SystemModel(ConfigService config, LogService log)
    {
        _config = config;
        _log = log;
    }

    public Services.LogLevel CurrentLevel { get; private set; }
    public int RetentionDays { get; private set; }
    public string CurrentTimeZone { get; private set; } = "";
    public List<TimeZoneInfo> Zones { get; private set; } = new();
    public List<string> Users { get; private set; } = new();
    public long LogFileBytes { get; private set; }

    [TempData] public string? Notice { get; set; }

    public void OnGet()
    {
        var (lvl, days, tz) = _config.Read(c => (c.Settings.MinLogLevel, c.Settings.LogRetentionDays, c.Settings.TimeZone));
        CurrentLevel = lvl;
        RetentionDays = days;
        CurrentTimeZone = tz ?? "";
        Zones = TimeZoneInfo.GetSystemTimeZones().OrderBy(z => z.Id, StringComparer.Ordinal).ToList();
        Users = _config.Read(c => c.Users.Select(u => u.Username).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList());
        LogFileBytes = _log.LogFileBytes();
    }

    public IActionResult OnPost(Services.LogLevel MinLogLevel, int LogRetentionDays, string? TimeZone)
    {
        _config.Mutate(c =>
        {
            c.Settings.MinLogLevel = MinLogLevel;
            c.Settings.LogRetentionDays = LogRetentionDays < 0 ? 0 : LogRetentionDays;
            c.Settings.TimeZone = (TimeZone ?? "").Trim();
        });
        _log.ApplyRetention();
        Notice = "Saved";
        return RedirectToPage();
    }

    public IActionResult OnPostClear()
    {
        _log.Clear();
        Notice = "Log cleared";
        return RedirectToPage();
    }
}
