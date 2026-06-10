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
    public long LogFileBytes { get; private set; }

    [TempData] public string? Notice { get; set; }

    public void OnGet()
    {
        var (lvl, days) = _config.Read(c => (c.Settings.MinLogLevel, c.Settings.LogRetentionDays));
        CurrentLevel = lvl;
        RetentionDays = days;
        LogFileBytes = _log.LogFileBytes();
    }

    public IActionResult OnPost(Services.LogLevel MinLogLevel, int LogRetentionDays)
    {
        _config.Mutate(c =>
        {
            c.Settings.MinLogLevel = MinLogLevel;
            c.Settings.LogRetentionDays = LogRetentionDays < 0 ? 0 : LogRetentionDays;
        });
        _log.ApplyRetention();
        Notice = "Gespeichert";
        return RedirectToPage();
    }

    public IActionResult OnPostClear()
    {
        _log.Clear();
        Notice = "Log cleared";
        return RedirectToPage();
    }
}
