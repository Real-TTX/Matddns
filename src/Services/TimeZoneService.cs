namespace Matddns.Services;

/// <summary>Converts UTC timestamps to the configured display time zone (UI only; the log stays UTC).</summary>
public class TimeZoneService
{
    private readonly ConfigService _config;
    private string? _cachedId;
    private TimeZoneInfo _cachedZone = TimeZoneInfo.Utc;

    public TimeZoneService(ConfigService config) => _config = config;

    public TimeZoneInfo Zone()
    {
        var id = _config.Read(c => c.Settings.TimeZone) ?? "";
        if (id != _cachedId)
        {
            _cachedId = id;
            try { _cachedZone = string.IsNullOrWhiteSpace(id) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { _cachedZone = TimeZoneInfo.Utc; }
        }
        return _cachedZone;
    }

    public DateTime ToLocal(DateTime utc)
    {
        var u = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(u, Zone());
    }

    /// <summary>Formats a UTC timestamp in the configured zone; null -> "—".</summary>
    public string Format(DateTime? utc, string fmt = "yyyy-MM-dd HH:mm:ss")
        => utc == null ? "—" : ToLocal(utc.Value).ToString(fmt);

    /// <summary>Short offset label of the configured zone, e.g. "UTC+02:00".</summary>
    public string Label()
    {
        var z = Zone();
        if (z.Id == "UTC") return "UTC";
        var off = z.GetUtcOffset(DateTime.UtcNow);
        var sign = off < TimeSpan.Zero ? "-" : "+";
        return $"UTC{sign}{Math.Abs(off.Hours):00}:{Math.Abs(off.Minutes):00}";
    }
}
