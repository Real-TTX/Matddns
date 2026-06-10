namespace Matddns.Services;

/// <summary>Version/build info baked in at image build time (env vars), shown in the footer.</summary>
public static class BuildInfo
{
    public static string Version { get; } = Get("MATDDNS_VERSION", "local");
    public static string Build { get; } = Get("MATDDNS_BUILD", "local");
    public static string Date { get; } = Get("MATDDNS_BUILD_DATE", "");

    public static bool IsRelease => !Version.Equals("local", StringComparison.OrdinalIgnoreCase);

    /// <summary>Footer text: "v0.1.42 · build 42 · 2026-06-10" (release) or "local · build local".</summary>
    public static string Display
    {
        get
        {
            var s = $"{(IsRelease ? "v" + Version : "local")} · build {Build}";
            if (!string.IsNullOrWhiteSpace(Date)) s += $" · {Date}";
            return s;
        }
    }

    private static string Get(string key, string fallback)
    {
        var v = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(v) ? fallback : v.Trim();
    }
}
