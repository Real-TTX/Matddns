using System.Text.Json;
using Matddns.Models;

namespace Matddns.Services;

public class ConfigService
{
    private readonly PathOptions _paths;
    private readonly AuthService _auth;
    private readonly object _lock = new();
    private AppConfig _config = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public ConfigService(PathOptions paths, AuthService auth)
    {
        _paths = paths;
        _auth = auth;
    }

    public AppConfig Current
    {
        get { lock (_lock) return CloneFrom(_config); }
    }

    public void EnsureLoaded()
    {
        lock (_lock)
        {
            if (File.Exists(_paths.ConfigFile))
            {
                try
                {
                    var json = File.ReadAllText(_paths.ConfigFile);
                    var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts);
                    if (cfg != null) _config = cfg;
                }
                catch { /* corrupt — keep defaults */ }
            }

            var changed = MigrateConfig(_config);

            // migrate the legacy single Auth account into the Users list
            if (_config.Users.Count == 0 && !string.IsNullOrEmpty(_config.Auth.PasswordHash))
            {
                _config.Users.Add(new UserAccount
                {
                    Username = string.IsNullOrWhiteSpace(_config.Auth.Username) ? "admin" : _config.Auth.Username,
                    PasswordHash = _config.Auth.PasswordHash
                });
                changed = true;
            }

            // seed default admin/admin if there are no users at all
            if (_config.Users.Count == 0)
            {
                _config.Users.Add(new UserAccount { Username = "admin", PasswordHash = _auth.Hash("admin") });
                changed = true;
            }

            if (changed) SaveInternal();
        }
    }

    /// <summary>One-time data fixes for legacy data. Returns true if anything changed.</summary>
    private static bool MigrateConfig(AppConfig cfg)
    {
        bool changed = false;
        foreach (var g in cfg.Domains)
        {
            foreach (var e in g.Entries)
            {
                // Legacy: the subdomain was in the RecordName override while Hostname was only the zone.
                // -> reconstruct the real FQDN and set the zone so display/update are correct.
                if (string.IsNullOrWhiteSpace(e.Domain)
                    && !string.IsNullOrWhiteSpace(e.RecordName)
                    && e.RecordName is not ("@" or "*")
                    && !string.IsNullOrWhiteSpace(e.Hostname)
                    && !e.Hostname.Equals(e.RecordName, StringComparison.OrdinalIgnoreCase)
                    && !e.Hostname.StartsWith(e.RecordName + ".", StringComparison.OrdinalIgnoreCase))
                {
                    e.Domain = e.Hostname;
                    e.Hostname = $"{e.RecordName}.{e.Hostname}";
                    changed = true;
                }
            }
        }
        return changed;
    }

    public void Mutate(Action<AppConfig> mutator)
    {
        lock (_lock)
        {
            mutator(_config);
            SaveInternal();
        }
    }

    public T Read<T>(Func<AppConfig, T> reader)
    {
        lock (_lock) return reader(_config);
    }

    private void SaveInternal()
    {
        var json = JsonSerializer.Serialize(_config, JsonOpts);
        var tmp = _paths.ConfigFile + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _paths.ConfigFile, true);
    }

    private static AppConfig CloneFrom(AppConfig src)
    {
        var json = JsonSerializer.Serialize(src, JsonOpts);
        return JsonSerializer.Deserialize<AppConfig>(json, JsonOpts)!;
    }
}
