namespace Matddns.Models;

public class AppConfig
{
    public int SchemaVersion { get; set; }                   // config schema version; migrated up on load (see ConfigService)
    public AuthSection Auth { get; set; } = new();           // legacy single account; migrated into Users on load
    public List<UserAccount> Users { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
    public List<SourceGroup> Sources { get; set; } = new();
    public List<DomainGroup> Domains { get; set; } = new();
    public List<Rule> Rules { get; set; } = new();
}

public class UserAccount
{
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = ""; // empty for an account MatIdentity created: no local sign-in until a password is set

    /// <summary>The claim <c>sub</c> of the person in MatIdentity this account is linked with; null = not linked (then not written to config.json).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ExternalSubject { get; set; }
}

public class AppSettings
{
    // fully qualified because LogLevel lives in Matddns.Services (conflict with Microsoft.Extensions.Logging.LogLevel)
    public Matddns.Services.LogLevel MinLogLevel { get; set; } = Matddns.Services.LogLevel.Info;
    public int LogRetentionDays { get; set; } = 14; // 0 = unlimited
    public string TimeZone { get; set; } = ""; // IANA id (e.g. Europe/Berlin); empty = UTC. Applies to UI times except the log.
    public bool AnonymousDashboard { get; set; } = false; // show the dashboard (read-only) without a login
    public NotificationSettings Notifications { get; set; } = new();
}

public class AuthSection
{
    public string Username { get; set; } = "admin";
    public string PasswordHash { get; set; } = "";
}

/// <summary>Who is told when a record gets a new IP or cannot be updated, and how.</summary>
public class NotificationSettings
{
    public bool OnChange { get; set; } = true;     // a record was written with a new value
    public bool OnFailure { get; set; } = true;    // a record cannot be updated (once, at the first failure) and when it works again
    public bool OnSource { get; set; } = true;     // a source entry (a WAN, a router, a peer) fails (once) and when it works again
    public MailSettings Mail { get; set; } = new();
    public WebhookSettings Webhook { get; set; } = new();
}

/// <summary>
/// Mail over SMTP. The fields are those of the <c>MATSMTP_*</c> variables of the Mat platform (host, port, username, password, from,
/// TLS none / starttls / tls), so mail through MatSMTP can replace them one to one (see <c>NotificationService.EffectiveMail</c>).
/// </summary>
public class MailSettings
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "";
    public MailTls Tls { get; set; } = MailTls.StartTls;
    public string To { get; set; } = "";           // one or more addresses, separated by commas
}

/// <summary>How the SMTP connection is secured, as <c>MATSMTP_TLS</c> names it: none, starttls, tls (TLS from the first byte).</summary>
public enum MailTls { None, StartTls, Tls }

public class WebhookSettings
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "";          // may carry a token (Gotify ?token=), so it is treated as a secret
    public WebhookFormat Format { get; set; } = WebhookFormat.Json;
}

/// <summary>Json: a JSON object for any receiver. Ntfy: plain text with a Title header (ntfy.sh topic URL). Gotify: its message JSON (URL with ?token=).</summary>
public enum WebhookFormat { Json, Ntfy, Gotify }
