using System.Text.Json;
using Matddns.Models;
using Matddns.Services;
using MatPlatform;
using LogLevel = Matddns.Services.LogLevel;

namespace Matddns.Platform;

/// <summary>
/// Matddns as an app of the platform contract (MatDesign docs/PLATFORM.md), on the shared package MatPlatform (folder <c>MatPlatform/</c>, a copy):
/// how Mattiverse shows Matddns (<see cref="Describe"/>: tiles, the few settings, buttons), the values behind it and what the buttons do.
/// The values come from <see cref="StatusService"/> and <see cref="SettingsService"/> (this class is the web layer: it reads no file itself);
/// the settings are the general ones of the System page, in config.json. Texts in German, in English with <c>Accept-Language: en</c>.
/// The engine API of Mattiverse (<c>/api/v1</c>) and the peer endpoints are not part of it.
/// </summary>
public sealed class MatddnsPlatform(
    StatusService status,
    SettingsService settings,
    ConfigService config,
    UpdaterService updater,
    LogService log,
    NotificationService notifications,
    IHostApplicationLifetime lifetime)
    : IMatPlatformState, IMatPlatformConfiguration, IMatPlatformActions, IMatPlatformHealth
{
    /// <summary>The id of Matddns in the platform (MatDesign AppRegistry.cs) and the default client id in MatIdentity.</summary>
    public const string AppId = "matddns";

    /// <summary>The accent from MatDesign AppRegistry.cs.</summary>
    public const string Accent = "#204db6";

    private static readonly string[] LogLevels = Enum.GetNames<LogLevel>();

    /// <summary>The MatApiDefinition of Matddns (<c>GET /api</c>) without MatSMTP; MatPlatform fills in <c>auth</c> and <c>endpoints</c>.</summary>
    public static void Describe(MatPlatformBuilder o) => Describe(o, new MatSmtpSettings());

    /// <summary>
    /// The definition with the mail through MatSMTP in <paramref name="smtp"/>: when it is configured, the mail server fields are fixed
    /// (<see cref="MatSmtpSettings.Fix"/>: shown, not changeable, <c>PUT /api/configure</c> refuses them); whether mail is on and the recipients stay editable.
    /// </summary>
    public static void Describe(MatPlatformBuilder o, MatSmtpSettings smtp)
    {
        var T = o.T;
        void Field(MatField field) => o.Field(NotificationSettingsChange.MailServerKeys.Contains(field.Key) ? smtp.Fix(field, o.Language) : field);

        o.App(
            AppId,
            "Matddns",
            T("DynDNS-Client mit Failover", "DynDNS client with failover"),
            BuildInfo.Version,
            "/favicon.svg",
            Accent);

        o.Page("overview", T("Übersicht", "Overview"), "dashboard",
            MatSection.Tiles("ipv4", "ipv6", "activeSource", "failovers", "records", "recordsInSync", "lastUpdate", "lastError"),
            MatSection.Actions("runNow", "open", "sources", "log"));
        o.Page("settings", T("Einstellungen", "Settings"), "settings",
            MatSection.Form(T("Allgemein", "General"), "timeZone", "anonymousDashboard"),
            MatSection.Form(T("Protokoll", "Log"), "minLogLevel", "logRetentionDays"),
            MatSection.Actions("openSystem"));
        o.Page("notifications", T("Benachrichtigungen", "Notifications"), "bell",
            MatSection.Form(T("Wann", "When"), "notifyOnChange", "notifyOnFailure", "notifyOnSource"),
            MatSection.Form(T("E-Mail", "E-mail"), "mailEnabled", "mailHost", "mailPort", "mailTls", "mailUsername", "mailPassword", "mailFrom", "mailTo"),
            MatSection.Form("Webhook", "webhookEnabled", "webhookFormat", "webhookUrl"),
            MatSection.Actions("openNotifications"));

        o.Tile("ipv4", T("Aktuelle IPv4", "Current IPv4"), "text", icon: "globe");
        o.Tile("ipv6", T("Aktuelle IPv6", "Current IPv6"), "text", icon: "globe");
        o.Tile("activeSource", T("Aktive Quelle", "Active source"), "text", icon: "network");
        o.Tile(new MatStateItem("failovers", T("Einträge im Failover", "Records in failover"), "number") { Icon = "route", WarnAbove = 0 });
        o.Tile("records", T("DNS-Einträge", "DNS records"), icon: "list");
        o.Tile(new MatStateItem("recordsInSync", T("Davon aktuell", "In sync"), "meter")
        {
            Icon = "check-circle",
            Max = JsonSerializer.SerializeToElement("records"),
        });
        o.Tile("lastUpdate", T("Letzte Aktualisierung", "Last update"), "time", icon: "clock");
        o.Tile("lastError", T("Letzter Fehler", "Last error"), "status", icon: "alert");

        o.Field(new MatField("timeZone", T("Zeitzone", "Time zone"), "text")
        {
            Max = 64,
            Placeholder = "Europe/Berlin",
            Hint = T("Für die Zeiten in der Oberfläche (IANA, etwa Europe/Berlin); leer = UTC. Das Protokoll bleibt in UTC.",
                "For the times in the UI (IANA, such as Europe/Berlin); empty = UTC. The log stays in UTC."),
        });
        o.Field(new MatField("anonymousDashboard", T("Übersicht ohne Anmeldung", "Dashboard without sign-in"), "bool")
        {
            Hint = T("Zeigt die Übersicht (nur lesend) auch ohne Anmeldung.", "Shows the dashboard (read-only) without a sign-in."),
        });
        o.Field(new MatField("minLogLevel", T("Kleinste Protokollstufe", "Minimum log level"), "select")
        {
            Required = true,
            Options =
            [
                new("Debug", T("Debug (alles)", "Debug (everything)")),
                new("Info", "Info"),
                new("Update", T("Update (nur Änderungen)", "Update (changes only)")),
                new("Warn", T("Warnung", "Warning")),
                new("Error", T("Fehler", "Error")),
            ],
        });
        o.Field(new MatField("logRetentionDays", T("Protokoll aufbewahren (Tage)", "Keep the log (days)"), "number")
        {
            Required = true,
            Min = 0,
            Max = SettingsService.MaxRetentionDays,
            Hint = T("0 = unbegrenzt.", "0 = unlimited."),
        });

        var mailOn = MatField.When(("mailEnabled", true));
        var hookOn = MatField.When(("webhookEnabled", true));
        o.Field(new MatField("notifyOnChange", T("Neue IP eines Eintrags", "A record gets a new IP"), "bool"));
        o.Field(new MatField("notifyOnFailure", T("Fehler und Erholung", "Failures and recoveries"), "bool")
        {
            Hint = T("Ein Eintrag lässt sich nicht aktualisieren oder klappt wieder.", "A record cannot be updated, or works again."),
        });
        o.Field(new MatField("notifyOnSource", T("Fehler und Erholung einer Quelle", "Source failures and recoveries"), "bool")
        {
            Hint = T("Eine Quelle (ein WAN, ein Router) fällt aus oder klappt wieder.", "A source (a WAN, a router) fails, or works again."),
        });
        o.Field(new MatField("mailEnabled", T("E-Mails senden", "Send e-mails"), "bool"));
        Field(new MatField("mailHost", T("SMTP-Server", "SMTP server"), "text") { Max = 255, Placeholder = "smtp.example.com", ShowIf = mailOn });
        Field(new MatField("mailPort", T("SMTP-Port", "SMTP port"), "number") { Min = 1, Max = 65535, ShowIf = mailOn });
        Field(new MatField("mailTls", T("Verschlüsselung", "Security"), "select")
        {
            Options = [new("starttls", "STARTTLS (587)"), new("tls", "TLS (465)"), new("none", T("Keine", "None"))],
            ShowIf = mailOn,
        });
        Field(new MatField("mailUsername", T("SMTP-Benutzer", "SMTP user"), "text") { Max = 255, ShowIf = mailOn });
        Field(new MatField("mailPassword", T("SMTP-Passwort", "SMTP password"), "secret") { ShowIf = mailOn });
        Field(new MatField("mailFrom", T("Absender", "From"), "email") { Max = 255, Placeholder = "matddns@example.com", ShowIf = mailOn });
        o.Field(new MatField("mailTo", T("Empfänger", "To"), "text")
        {
            Max = 1000,
            Placeholder = "admin@example.com, ops@example.com",
            Hint = T("Eine oder mehrere Adressen, mit Komma getrennt.", "One or more addresses, separated by commas."),
            ShowIf = mailOn,
        });
        o.Field(new MatField("webhookEnabled", T("Webhook aufrufen", "Call a webhook"), "bool"));
        o.Field(new MatField("webhookFormat", T("Format", "Format"), "select")
        {
            Options = [new("json", "JSON"), new("ntfy", "ntfy"), new("gotify", "Gotify")],
            ShowIf = hookOn,
        });
        o.Field(new MatField("webhookUrl", "URL", "secret")
        {
            Hint = T("Kann ein Token tragen und wird deshalb nie zurückgegeben.", "May carry a token, so it is never sent back."),
            ShowIf = hookOn,
        });

        o.PostAction("runNow",T("Jetzt aktualisieren", "Update now"), "refresh");
        o.LinkAction("open", T("Matddns öffnen", "Open Matddns"), "external", "/");
        o.LinkAction("sources", T("Quellen", "Sources"), "network", "/Sources");
        o.LinkAction("log", T("Protokoll", "Log"), "file-text", "/Log");
        o.LinkAction("openSystem", T("Alle Einstellungen", "All settings"), "settings", "/System");
        o.LinkAction("openNotifications", T("Benachrichtigungen öffnen", "Open notifications"), "bell", "/System?tab=notifications");
    }

    public Task<IReadOnlyList<MatHealthCheck>> CheckHealthAsync(MatPlatformCall call, CancellationToken ct)
        => Task.FromResult(HealthChecks(call, status.Build(0), config.SaveBlocked));

    /// <summary>
    /// The checks of <c>/api/health</c>: <c>config</c> is down when config.json cannot be read (Matddns runs on an empty config and saves nothing);
    /// <c>sources</c> and <c>rules</c> are degraded while a source entry or a record fails (the old monitoring status "degraded").
    /// </summary>
    internal static IReadOnlyList<MatHealthCheck> HealthChecks(MatPlatformCall call, StatusSnapshot s, bool saveBlocked)
    {
        var checks = new List<MatHealthCheck>
        {
            saveBlocked
                ? new MatHealthCheck("config", "down", call.T("config.json ist nicht lesbar; Matddns speichert nichts.", "config.json cannot be read; Matddns saves nothing."))
                : new MatHealthCheck("config", "ok"),
            s.Sources.Errors > 0
                ? new MatHealthCheck("sources", "degraded", call.T(
                    $"{s.Sources.Errors} von {s.Sources.Entries} Quellen mit Fehler.", $"{s.Sources.Errors} of {s.Sources.Entries} sources failing."))
                : new MatHealthCheck("sources", "ok"),
            s.Rules.Issues > 0
                ? new MatHealthCheck("rules", "degraded", call.T(
                    $"{s.Rules.Issues} von {s.Rules.Total} DNS-Einträgen mit Fehler.", $"{s.Rules.Issues} of {s.Rules.Total} DNS records failing."))
                : new MatHealthCheck("rules", "ok"),
        };
        return checks;
    }

    public Task<IReadOnlyDictionary<string, MatStateValue>> GetStateAsync(MatPlatformCall call, CancellationToken ct)
    {
        var o = status.Overview();
        var recentError = o.LastError is { } e && e.Timestamp >= DateTime.UtcNow.AddHours(-24) ? e : null;

        IReadOnlyDictionary<string, MatStateValue> values = new Dictionary<string, MatStateValue>
        {
            ["ipv4"] = new(o.Ipv4),
            ["ipv6"] = new(o.Ipv6),
            ["activeSource"] = new(o.ActiveSource),
            ["failovers"] = new(o.Failovers, o.Failovers > 0 ? "warn" : "ok"),
            ["records"] = new(o.Records),
            ["recordsInSync"] = new(o.RecordsInSync, o.RecordsInSync < o.Records ? "warn" : "ok"),
            ["lastUpdate"] = new(Utc(o.LastUpdate)),
            ["lastError"] = recentError is null
                ? new("ok", "ok", call.T("Keine Fehler in den letzten 24 Stunden.", "No errors in the last 24 hours."))
                : new("error", "error", Clip($"{recentError.Timestamp:yyyy-MM-dd HH:mm} UTC · {recentError.Source}: {recentError.Message}")),
        };
        return Task.FromResult(values);
    }

    public Task<IReadOnlyDictionary<string, object?>> GetConfigurationAsync(MatPlatformCall call, CancellationToken ct)
    {
        var s = settings.Get();
        var n = settings.GetNotifications();
        // The values in force: MatSMTP's server, user, password and sender when Mattiverse provides it.
        var mail = notifications.EffectiveMail();
        IReadOnlyDictionary<string, object?> values = new Dictionary<string, object?>
        {
            ["timeZone"] = s.TimeZone,
            ["anonymousDashboard"] = s.AnonymousDashboard,
            ["minLogLevel"] = s.MinLogLevel.ToString(),
            ["logRetentionDays"] = s.LogRetentionDays,
            ["notifyOnChange"] = n.OnChange,
            ["notifyOnFailure"] = n.OnFailure,
            ["notifyOnSource"] = n.OnSource,
            ["mailEnabled"] = mail.Enabled,
            ["mailHost"] = mail.Host,
            ["mailPort"] = mail.Port,
            ["mailTls"] = TlsName(mail.Tls),
            ["mailUsername"] = mail.Username,
            ["mailPassword"] = MatSecret.Of(mail.Password),
            ["mailFrom"] = mail.From,
            ["mailTo"] = mail.To,
            ["webhookEnabled"] = n.Webhook.Enabled,
            ["webhookFormat"] = n.Webhook.Format.ToString().ToLowerInvariant(),
            ["webhookUrl"] = MatSecret.Of(n.Webhook.Url),
        };
        return Task.FromResult(values);
    }

    private static string TlsName(MailTls tls) => tls switch
    {
        MailTls.Tls => "tls",
        MailTls.None => "none",
        _ => "starttls",
    };

    /// <summary>The change of the notification settings in <paramref name="values"/>, or null when there is none; a value of the wrong kind goes to <paramref name="errors"/>.</summary>
    private static NotificationSettingsChange? NotificationChange(IReadOnlyDictionary<string, JsonElement> values, Dictionary<string, string> errors, MatPlatformCall call)
    {
        var any = false;

        bool? Bool(string key)
        {
            if (!values.TryGetValue(key, out var v)) return null;
            any = true;
            if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.ValueKind == JsonValueKind.True;
            errors[key] = call.T("Bitte true oder false angeben.", "Send true or false.");
            return null;
        }

        string? Text(string key)
        {
            if (!values.TryGetValue(key, out var v)) return null;
            any = true;
            if (v.ValueKind == JsonValueKind.Null) return "";
            if (v.ValueKind == JsonValueKind.String) return v.GetString() ?? "";
            errors[key] = call.T("Bitte einen Text angeben.", "Send a text.");
            return null;
        }

        int? port = null;
        if (values.TryGetValue("mailPort", out var p))
        {
            any = true;
            if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var number)) port = number;
            else errors["mailPort"] = call.T("Bitte eine ganze Zahl angeben.", "Send a whole number.");
        }

        MailTls? tls = Text("mailTls") switch
        {
            null => null,
            "starttls" => MailTls.StartTls,
            "tls" => MailTls.Tls,
            "none" => MailTls.None,
            _ => Refuse<MailTls>("mailTls"),
        };
        WebhookFormat? format = Text("webhookFormat") switch
        {
            null => null,
            "json" => WebhookFormat.Json,
            "ntfy" => WebhookFormat.Ntfy,
            "gotify" => WebhookFormat.Gotify,
            _ => Refuse<WebhookFormat>("webhookFormat"),
        };

        T? Refuse<T>(string key) where T : struct
        {
            errors[key] = call.T("Bitte einen der angebotenen Werte wählen.", "Choose one of the offered values.");
            return null;
        }

        var change = new NotificationSettingsChange(
            OnChange: Bool("notifyOnChange"),
            OnFailure: Bool("notifyOnFailure"),
            OnSource: Bool("notifyOnSource"),
            MailEnabled: Bool("mailEnabled"),
            MailHost: Text("mailHost"),
            MailPort: port,
            MailTls: tls,
            MailUsername: Text("mailUsername"),
            MailPassword: Text("mailPassword"),
            MailFrom: Text("mailFrom"),
            MailTo: Text("mailTo"),
            WebhookEnabled: Bool("webhookEnabled"),
            WebhookUrl: Text("webhookUrl"),
            WebhookFormat: format);
        return any ? change : null;
    }

    /// <summary>
    /// Saves the changed settings. MatPlatform has checked them against the definition (type, required, limits, options); here: whole numbers
    /// and a time zone this system knows (<see cref="SettingsService"/>). Nothing is saved when a value is wrong.
    /// </summary>
    public Task<IReadOnlyDictionary<string, string>> ConfigureAsync(IReadOnlyDictionary<string, JsonElement> values, MatPlatformCall call, CancellationToken ct)
    {
        var errors = new Dictionary<string, string>();

        int? days = null;
        if (values.TryGetValue("logRetentionDays", out var d))
        {
            if (d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var n)) days = n;
            else errors["logRetentionDays"] = call.T("Bitte eine ganze Zahl angeben.", "Send a whole number.");
        }

        LogLevel? level = null;
        if (values.TryGetValue("minLogLevel", out var l))
        {
            if (l.ValueKind == JsonValueKind.String && LogLevels.Contains(l.GetString()) && Enum.TryParse<LogLevel>(l.GetString(), out var parsed)) level = parsed;
            else errors["minLogLevel"] = call.T("Unbekannte Stufe.", "Unknown level.");
        }

        string? zone = null;
        if (values.TryGetValue("timeZone", out var z))
        {
            zone = z.ValueKind switch
            {
                JsonValueKind.String => z.GetString() ?? "",
                JsonValueKind.Null => "",
                _ => null,
            };
            if (zone == null) errors["timeZone"] = call.T("Bitte einen Text angeben.", "Send a text.");
            else if (!SettingsService.IsKnownTimeZone(zone)) errors["timeZone"] = call.T("Unbekannte Zeitzone (etwa Europe/Berlin, leer = UTC).", "Unknown time zone (such as Europe/Berlin, empty = UTC).");
        }

        bool? anonymous = values.TryGetValue("anonymousDashboard", out var a) && a.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? a.ValueKind == JsonValueKind.True
            : null;

        var change = NotificationChange(values, errors, call);
        if (errors.Count > 0) return Task.FromResult<IReadOnlyDictionary<string, string>>(errors);

        // The notifications first: they are checked as a whole (the general values were checked above), so nothing is saved when one is wrong.
        if (change is not null)
        {
            var refused = settings.ChangeNotifications(change, $"the platform API ({call.Caller})");
            if (refused.Count > 0)
            {
                // A fixed field (MatSMTP): MatPlatform refuses it already; this keeps it so and says where the mail goes.
                var texts = refused.ToDictionary(e => e.Key, e => NotificationSettingsChange.MailServerKeys.Contains(e.Key) && notifications.MailThroughMatSmtp
                    ? notifications.MatSmtpText(call.Language)!
                    : e.Value);
                return Task.FromResult<IReadOnlyDictionary<string, string>>(texts);
            }
        }

        var problems = settings.Change(new GeneralSettingsChange(level, days, zone, anonymous), $"the platform API ({call.Caller})");
        return Task.FromResult(problems);
    }

    public Task<MatActionResult?> RunActionAsync(string id, MatPlatformCall call, CancellationToken ct)
    {
        if (id != "runNow") return Task.FromResult<MatActionResult?>(null);

        // A pass asks the sources and writes the DNS records: that can take longer than a caller waits. Started here, the result shows in /api/state.
        log.Log(LogLevel.Info, "api", $"update now (platform API, {call.Caller})");
        var stopping = lifetime.ApplicationStopping;
        _ = Task.Run(async () =>
        {
            try { await updater.RunNowAsync(stopping); }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            catch (Exception ex) { log.Log(LogLevel.Error, "api", $"update now: {ex.Message}"); }
        }, CancellationToken.None);

        return Task.FromResult<MatActionResult?>(new MatActionResult(true, call.T(
            "Gestartet: alle Quellen und DNS-Einträge werden jetzt geprüft.", "Started: every source and DNS record is checked now.")));
    }

    private static DateTimeOffset? Utc(DateTime? time)
        => time is { } t ? new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)) : null;

    private static string Clip(string text) => text.Length <= 300 ? text : text[..297] + "...";
}
