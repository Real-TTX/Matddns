using Matddns.Models;
using MatPlatform;

namespace Matddns.Services;

/// <summary>The general settings (System page) as values: what the Mat platform contract reads and changes (<c>/api/configure</c>).</summary>
public sealed record GeneralSettings(LogLevel MinLogLevel, int LogRetentionDays, string TimeZone, bool AnonymousDashboard);

/// <summary>A change of the general settings: null = unchanged.</summary>
public sealed record GeneralSettingsChange(LogLevel? MinLogLevel = null, int? LogRetentionDays = null, string? TimeZone = null, bool? AnonymousDashboard = null);

/// <summary>
/// A change of the notification settings: null = unchanged (so an empty password or webhook URL of a form is passed as null to keep the stored one;
/// <c>""</c> clears it). The keys of <see cref="SettingsService.ChangeNotifications"/> are those of <c>/api/configure</c>.
/// </summary>
public sealed record NotificationSettingsChange(
    bool? OnChange = null,
    bool? OnFailure = null,
    bool? MailEnabled = null,
    string? MailHost = null,
    int? MailPort = null,
    MailTls? MailTls = null,
    string? MailUsername = null,
    string? MailPassword = null,
    string? MailFrom = null,
    string? MailTo = null,
    bool? WebhookEnabled = null,
    string? WebhookUrl = null,
    WebhookFormat? WebhookFormat = null)
{
    /// <summary>The keys of the mail server settings that MatSMTP fixes (<c>/api/configure</c>).</summary>
    public static readonly IReadOnlyList<string> MailServerKeys = ["mailHost", "mailPort", "mailTls", "mailUsername", "mailPassword", "mailFrom"];

    /// <summary>The keys of <see cref="MailServerKeys"/> this change sets.</summary>
    public IEnumerable<string> ServerKeys()
    {
        if (MailHost != null) yield return "mailHost";
        if (MailPort != null) yield return "mailPort";
        if (MailTls != null) yield return "mailTls";
        if (MailUsername != null) yield return "mailUsername";
        if (MailPassword != null) yield return "mailPassword";
        if (MailFrom != null) yield return "mailFrom";
    }
}

/// <summary>
/// Reads and changes the general and the notification settings of <see cref="AppSettings"/> in config.json, with the same effect for the System page
/// and the platform contract (the retention of the log is applied right away), and writes who changed what to the log.
/// </summary>
public class SettingsService
{
    /// <summary>The longest retention of the log that can be set (ten years).</summary>
    public const int MaxRetentionDays = 3650;

    private readonly ConfigService _config;
    private readonly LogService _log;
    private readonly MatSmtpSettings _smtp;

    public SettingsService(ConfigService config, LogService log, MatSmtpSettings? smtp = null)
    {
        _config = config;
        _log = log;
        _smtp = smtp ?? new MatSmtpSettings();
    }

    public GeneralSettings Get()
        => _config.Read(c => new GeneralSettings(c.Settings.MinLogLevel, c.Settings.LogRetentionDays, c.Settings.TimeZone ?? "", c.Settings.AnonymousDashboard));

    /// <summary>Whether <paramref name="id"/> is a time zone this system knows (empty = UTC).</summary>
    public static bool IsKnownTimeZone(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return true;
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks and saves <paramref name="change"/>; answers the problems by key (<c>logRetentionDays</c>, <c>timeZone</c>, <c>minLogLevel</c>) and saves
    /// nothing when there is one. <paramref name="who"/> names the caller in the log.
    /// </summary>
    public IReadOnlyDictionary<string, string> Change(GeneralSettingsChange change, string who)
    {
        var problems = new Dictionary<string, string>();
        if (change.LogRetentionDays is < 0 or > MaxRetentionDays)
            problems["logRetentionDays"] = $"Between 0 and {MaxRetentionDays} days.";
        if (change.TimeZone != null && !IsKnownTimeZone(change.TimeZone))
            problems["timeZone"] = "Unknown time zone (an IANA id such as Europe/Berlin, or empty for UTC).";
        if (change.MinLogLevel is { } level && !Enum.IsDefined(level))
            problems["minLogLevel"] = "Unknown log level.";
        if (problems.Count > 0) return problems;

        var changed = new List<string>();
        _config.Mutate(c =>
        {
            if (change.MinLogLevel is { } lvl && lvl != c.Settings.MinLogLevel) { c.Settings.MinLogLevel = lvl; changed.Add($"minimum log level {lvl}"); }
            if (change.LogRetentionDays is { } days && days != c.Settings.LogRetentionDays) { c.Settings.LogRetentionDays = days; changed.Add($"log retention {days} days"); }
            if (change.TimeZone != null && change.TimeZone.Trim() != (c.Settings.TimeZone ?? ""))
            {
                c.Settings.TimeZone = change.TimeZone.Trim();
                changed.Add($"time zone {(c.Settings.TimeZone.Length == 0 ? "UTC" : c.Settings.TimeZone)}");
            }
            if (change.AnonymousDashboard is { } anon && anon != c.Settings.AnonymousDashboard) { c.Settings.AnonymousDashboard = anon; changed.Add($"anonymous dashboard {(anon ? "on" : "off")}"); }
        });

        if (changed.Count > 0)
        {
            _log.ApplyRetention();
            _log.Log(LogLevel.Info, "settings", $"changed by {who}: {string.Join(", ", changed)}");
        }

        return problems;
    }

    /// <summary>The settings of System, Notifications as they are stored (the mail server ones may be overridden by MatSMTP: <see cref="NotificationService.EffectiveMail"/>).</summary>
    public NotificationSettings GetNotifications() => _config.Read(c => Copy(c.Settings.Notifications));

    /// <summary>True when the mail goes through MatSMTP and the server settings of the mail are fixed.</summary>
    public bool MailFixed => _smtp.IsConfigured;

    /// <summary>
    /// Checks and saves <paramref name="change"/> of the notification settings; answers the problems by key (<c>mailHost</c>, <c>mailPort</c>, <c>mailFrom</c>,
    /// <c>mailTo</c>, <c>webhookUrl</c>, or a fixed key) and saves nothing when there is one. When the mail goes through MatSMTP a change of a server field
    /// (host, port, security, user, password, sender) is refused; the page simply does not send them. Mail is checked as it would be sent (with MatSMTP's values).
    /// </summary>
    public IReadOnlyDictionary<string, string> ChangeNotifications(NotificationSettingsChange change, string who)
    {
        var problems = new Dictionary<string, string>();
        if (_smtp.IsConfigured)
        {
            foreach (var key in change.ServerKeys()) problems[key] = _smtp.Describe("en")!;
            if (problems.Count > 0) return problems;
        }

        var current = GetNotifications();
        var mail = current.Mail;
        if (change.MailEnabled is { } on) mail.Enabled = on;
        if (change.MailHost != null) mail.Host = change.MailHost.Trim();
        if (change.MailPort is { } port) mail.Port = port;
        if (change.MailTls is { } tls) mail.Tls = tls;
        if (change.MailUsername != null) mail.Username = change.MailUsername.Trim();
        if (change.MailPassword != null) mail.Password = change.MailPassword;
        if (change.MailFrom != null) mail.From = change.MailFrom.Trim();
        if (change.MailTo != null) mail.To = change.MailTo.Trim();

        var hook = current.Webhook;
        if (change.WebhookEnabled is { } hookOn) hook.Enabled = hookOn;
        if (change.WebhookFormat is { } format) hook.Format = format;
        if (change.WebhookUrl != null) hook.Url = change.WebhookUrl.Trim();

        if (!Enum.IsDefined(mail.Tls)) problems["mailTls"] = "Unknown security.";
        if (!Enum.IsDefined(hook.Format)) problems["webhookFormat"] = "Unknown format.";
        if (mail.Enabled && MailProblem(NotificationService.Effective(mail, _smtp)) is { } p) problems[p.Key] = $"E-mail: {p.Message}.";
        if (hook.Enabled && !InputCheck.IsHttpUrl(hook.Url)) problems["webhookUrl"] = "Webhook: enter an http(s) URL.";
        if (problems.Count > 0) return problems;

        var saved = new NotificationSettings
        {
            OnChange = change.OnChange ?? current.OnChange,
            OnFailure = change.OnFailure ?? current.OnFailure,
            Mail = mail,
            Webhook = hook,
        };
        _config.Mutate(c => c.Settings.Notifications = saved);
        _log.Log(LogLevel.Info, "settings", $"notifications changed by {who}: e-mail {(mail.Enabled ? (_smtp.IsConfigured ? "on (MatSMTP)" : "on") : "off")}, webhook {(hook.Enabled ? hook.Format.ToString() : "off")}");
        return problems;
    }

    /// <summary>What is missing for mail with the key of the field it belongs to, or null (<see cref="NotificationService.MailProblem"/>).</summary>
    private static (string Key, string Message)? MailProblem(MailSettings m)
    {
        if (NotificationService.MailProblem(m) is not { } problem) return null;
        var key = string.IsNullOrWhiteSpace(m.Host) ? "mailHost"
            : m.Port is < 1 or > 65535 ? "mailPort"
            : problem.Contains("sender") ? "mailFrom"
            : "mailTo";
        return (key, problem);
    }

    private static NotificationSettings Copy(NotificationSettings n) => new()
    {
        OnChange = n.OnChange,
        OnFailure = n.OnFailure,
        Mail = new MailSettings
        {
            Enabled = n.Mail.Enabled,
            Host = n.Mail.Host,
            Port = n.Mail.Port,
            Username = n.Mail.Username,
            Password = n.Mail.Password,
            From = n.Mail.From,
            Tls = n.Mail.Tls,
            To = n.Mail.To,
        },
        Webhook = new WebhookSettings { Enabled = n.Webhook.Enabled, Url = n.Webhook.Url, Format = n.Webhook.Format },
    };
}
