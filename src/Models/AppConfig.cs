namespace Matddns.Models;

public class AppConfig
{
    public AuthSection Auth { get; set; } = new();
    public AppSettings Settings { get; set; } = new();
    public List<SourceGroup> Sources { get; set; } = new();
    public List<DomainGroup> Domains { get; set; } = new();
    public List<Rule> Rules { get; set; } = new();
}

public class AppSettings
{
    // voll qualifiziert, da LogLevel in Matddns.Services lebt (Konflikt mit Microsoft.Extensions.Logging.LogLevel)
    public Matddns.Services.LogLevel MinLogLevel { get; set; } = Matddns.Services.LogLevel.Info;
    public int LogRetentionDays { get; set; } = 14; // 0 = unbegrenzt
}

public class AuthSection
{
    public string Username { get; set; } = "admin";
    public string PasswordHash { get; set; } = "";
}
