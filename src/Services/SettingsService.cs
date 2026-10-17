using Matddns.Models;

namespace Matddns.Services;

/// <summary>The general settings (System page) as values: what the Mat platform contract reads and changes (<c>/api/configure</c>).</summary>
public sealed record GeneralSettings(LogLevel MinLogLevel, int LogRetentionDays, string TimeZone, bool AnonymousDashboard);

/// <summary>A change of the general settings: null = unchanged.</summary>
public sealed record GeneralSettingsChange(LogLevel? MinLogLevel = null, int? LogRetentionDays = null, string? TimeZone = null, bool? AnonymousDashboard = null);

/// <summary>
/// Reads and changes the general settings of <see cref="AppSettings"/> in config.json, with the same effect as the System page
/// (the retention of the log is applied right away), and writes who changed what to the log. Used by the platform contract.
/// </summary>
public class SettingsService
{
    /// <summary>The longest retention of the log that can be set (ten years).</summary>
    public const int MaxRetentionDays = 3650;

    private readonly ConfigService _config;
    private readonly LogService _log;

    public SettingsService(ConfigService config, LogService log)
    {
        _config = config;
        _log = log;
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
}
