using Matddns.Models;
using Matddns.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matddns.Pages;

public class SystemModel : PageModel
{
    private readonly ConfigService _config;
    private readonly LogService _log;
    private readonly AuthService _auth;
    public SystemModel(ConfigService config, LogService log, AuthService auth)
    {
        _config = config;
        _log = log;
        _auth = auth;
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

    public IActionResult OnPostAddUser(string NewUsername, string NewPassword)
    {
        var name = (NewUsername ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(NewPassword))
        {
            Notice = "Username and password required";
            return RedirectToPage();
        }
        var exists = _config.Read(c => c.Users.Any(u => u.Username.Equals(name, StringComparison.OrdinalIgnoreCase)));
        if (exists) { Notice = $"User '{name}' already exists"; return RedirectToPage(); }

        _config.Mutate(c => c.Users.Add(new UserAccount { Username = name, PasswordHash = _auth.Hash(NewPassword) }));
        Notice = $"User '{name}' added";
        return RedirectToPage();
    }

    public IActionResult OnPostSetPassword(string Username, string Password)
    {
        if (string.IsNullOrEmpty(Password)) { Notice = "Password required"; return RedirectToPage(); }
        _config.Mutate(c =>
        {
            var u = c.Users.FirstOrDefault(x => x.Username.Equals(Username, StringComparison.OrdinalIgnoreCase));
            if (u != null) u.PasswordHash = _auth.Hash(Password);
        });
        Notice = $"Password for '{Username}' updated";
        return RedirectToPage();
    }

    public IActionResult OnPostDeleteUser(string Username)
    {
        _config.Mutate(c =>
        {
            // never remove the last account (lock-out protection)
            if (c.Users.Count > 1)
                c.Users.RemoveAll(u => u.Username.Equals(Username, StringComparison.OrdinalIgnoreCase));
        });
        Notice = $"User '{Username}' removed";
        return RedirectToPage();
    }
}
