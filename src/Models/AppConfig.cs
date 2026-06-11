namespace Matddns.Models;

public class AppConfig
{
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
    public string PasswordHash { get; set; } = "";
}

public class AppSettings
{
    // fully qualified because LogLevel lives in Matddns.Services (conflict with Microsoft.Extensions.Logging.LogLevel)
    public Matddns.Services.LogLevel MinLogLevel { get; set; } = Matddns.Services.LogLevel.Info;
    public int LogRetentionDays { get; set; } = 14; // 0 = unlimited
    public string TimeZone { get; set; } = ""; // IANA id (e.g. Europe/Berlin); empty = UTC. Applies to UI times except the log.
    public bool AnonymousDashboard { get; set; } = false; // show the dashboard (read-only) without a login
}

public class AuthSection
{
    public string Username { get; set; } = "admin";
    public string PasswordHash { get; set; } = "";
}
