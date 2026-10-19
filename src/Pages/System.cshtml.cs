using Matddns.Models;
using Matddns.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matddns.Pages;

public class SystemModel : PageModel
{
    /// <summary>The tabs of the page: the id (also <c>?tab=</c>, so a form comes back to its tab) and the title.</summary>
    public static readonly (string Id, string Title)[] Tabs = [("general", "General"), ("notifications", "Notifications"), ("backup", "Backup"), ("api", "API")];

    private readonly SettingsService _settings;
    private readonly LogService _log;
    private readonly ConfigService _config;
    private readonly NotificationService _notify;

    public SystemModel(SettingsService settings, LogService log, ConfigService config, NotificationService notify)
    {
        _settings = settings;
        _log = log;
        _config = config;
        _notify = notify;
    }

    public string Tab { get; private set; } = "general";
    public Services.LogLevel CurrentLevel { get; private set; }
    public int RetentionDays { get; private set; }
    public string CurrentTimeZone { get; private set; } = "";
    public bool AnonymousDashboard { get; private set; }
    public List<TimeZoneInfo> Zones { get; private set; } = new();
    public long LogFileBytes { get; private set; }
    public NotificationSettings Notifications { get; private set; } = new();

    /// <summary>The mail settings in force (with MatSMTP its values), the password emptied: what the form shows.</summary>
    public MailSettings Mail { get; private set; } = new();
    public bool HasMailPassword { get; private set; }

    /// <summary>True when the mail goes through MatSMTP: the server fields are shown disabled and nothing is saved for them.</summary>
    public bool MailFixed => _notify.MailThroughMatSmtp;

    /// <summary>The sentence that says where the mail goes when <see cref="MailFixed"/>.</summary>
    public string? MailFixedText => _notify.MatSmtpText("en");

    [TempData] public string? Notice { get; set; }
    [TempData] public string? Error { get; set; }

    public void OnGet(string? tab)
    {
        Tab = Tabs.Any(t => t.Id == tab) ? tab! : "general";
        var s = _settings.Get();
        CurrentLevel = s.MinLogLevel;
        RetentionDays = s.LogRetentionDays;
        CurrentTimeZone = s.TimeZone;
        AnonymousDashboard = s.AnonymousDashboard;
        Zones = TimeZoneInfo.GetSystemTimeZones().OrderBy(z => z.Id, StringComparer.Ordinal).ToList();
        LogFileBytes = _log.LogFileBytes();
        Notifications = _settings.GetNotifications();
        // With MatSMTP the form shows its server, port, security, user and sender (never the password); the stored ones stay stored.
        Mail = _notify.EffectiveMail();
        HasMailPassword = !string.IsNullOrEmpty(Mail.Password);
        Mail.Password = "";
    }

    /// <summary>Only the scheme and the host of a URL that may carry a token (<c>https://ntfy.sh/…</c>).</summary>
    public static string UrlHint(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Host}/…" : "set";

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

    /// <summary>Downloads the configuration (sources, domains, rules, settings with their credentials; no accounts).</summary>
    public IActionResult OnGetExport()
    {
        _log.Log(Services.LogLevel.Info, "settings", $"configuration exported by {User.Identity?.Name ?? "?"}");
        var bytes = System.Text.Encoding.UTF8.GetBytes(_config.ExportJson());
        return File(bytes, "application/json", $"matddns-{DateTime.UtcNow:yyyy-MM-dd}.json");
    }

    /// <summary>The largest file an import takes.</summary>
    public const int MaxImportBytes = 5 * 1024 * 1024;

    public async Task<IActionResult> OnPostImportAsync(IFormFile? file, CancellationToken ct)
    {
        if (file == null || file.Length == 0) { Error = "Choose a file."; return RedirectToPage(new { tab = "backup" }); }
        if (file.Length > MaxImportBytes) { Error = "The file is larger than 5 MB."; return RedirectToPage(new { tab = "backup" }); }

        using var reader = new StreamReader(file.OpenReadStream());
        var json = await reader.ReadToEndAsync(ct);
        try
        {
            var r = _config.Import(json);
            _log.Log(Services.LogLevel.Info, "settings", $"configuration imported by {User.Identity?.Name ?? "?"}: {r.Sources} sources, {r.Domains} domain groups, {r.Rules} rules");
            Notice = $"Imported {r.Sources} sources, {r.Domains} domain groups and {r.Rules} rules.";
        }
        catch (FormatException ex) { Error = ex.Message; }
        return RedirectToPage(new { tab = "backup" });
    }

    public IActionResult OnPostNotifications(NotificationForm form)
    {
        if (SaveNotifications(form) is { } problem) Error = problem;
        else Notice = "Saved.";
        return RedirectToPage(new { tab = "notifications" });
    }

    public async Task<IActionResult> OnPostTestNotificationsAsync(NotificationForm form, CancellationToken ct)
    {
        if (SaveNotifications(form) is { } problem) { Error = problem; return RedirectToPage(new { tab = "notifications" }); }
        if (!form.MailEnabled && !form.WebhookEnabled) { Error = "Switch on e-mail or the webhook first."; return RedirectToPage(new { tab = "notifications" }); }

        var failed = await _notify.SendAsync(new Notification(NotificationKind.Test, "test message",
            $"This is a test of the notifications of Matddns, sent by {User.Identity?.Name ?? "?"}."), ct);
        if (failed.Count > 0) Error = "Saved, but the test failed. " + string.Join(" ", failed);
        else Notice = "Saved. The test message was sent.";
        return RedirectToPage(new { tab = "notifications" });
    }

    /// <summary>
    /// Checks and saves the form (<see cref="SettingsService.ChangeNotifications"/>, the same as <c>/api/configure</c>); answers the problem (nothing saved) or null.
    /// An empty password or URL keeps the stored one. With MatSMTP the server fields are disabled in the form and not taken from it at all.
    /// </summary>
    private string? SaveNotifications(NotificationForm f)
    {
        var server = !_settings.MailFixed;
        var change = new NotificationSettingsChange(
            OnChange: f.OnChange,
            OnFailure: f.OnFailure,
            OnSource: f.OnSource,
            MailEnabled: f.MailEnabled,
            MailHost: server ? f.MailHost ?? "" : null,
            MailPort: server ? f.MailPort : null,
            MailTls: server ? f.MailTls : null,
            MailUsername: server ? f.MailUsername ?? "" : null,
            MailPassword: server && !string.IsNullOrEmpty(f.MailPassword) ? f.MailPassword : null,
            MailFrom: server ? f.MailFrom ?? "" : null,
            MailTo: f.MailTo ?? "",
            WebhookEnabled: f.WebhookEnabled,
            WebhookUrl: string.IsNullOrWhiteSpace(f.WebhookUrl) ? null : f.WebhookUrl,
            WebhookFormat: f.WebhookFormat);
        var problems = _settings.ChangeNotifications(change, User.Identity?.Name ?? "?");
        return problems.Count == 0 ? null : string.Join(" ", problems.Values.Distinct());
    }

    public sealed class NotificationForm
    {
        public bool OnChange { get; set; }
        public bool OnFailure { get; set; }
        public bool OnSource { get; set; }
        public bool MailEnabled { get; set; }
        public string? MailHost { get; set; }
        public int MailPort { get; set; } = 587;
        public MailTls MailTls { get; set; } = MailTls.StartTls;
        public string? MailUsername { get; set; }
        public string? MailPassword { get; set; }
        public string? MailFrom { get; set; }
        public string? MailTo { get; set; }
        public bool WebhookEnabled { get; set; }
        public string? WebhookUrl { get; set; }
        public WebhookFormat WebhookFormat { get; set; } = WebhookFormat.Json;
    }
}
