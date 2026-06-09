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

            if (string.IsNullOrEmpty(_config.Auth.PasswordHash))
            {
                _config.Auth.Username = "admin";
                _config.Auth.PasswordHash = _auth.Hash("admin");
                changed = true;
            }

            if (changed) SaveInternal();
        }
    }

    /// <summary>Einmalige Datenkorrekturen für Altbestände. Gibt true zurück, wenn etwas geändert wurde.</summary>
    private static bool MigrateConfig(AppConfig cfg)
    {
        bool changed = false;
        foreach (var g in cfg.Domains)
        {
            foreach (var e in g.Entries)
            {
                // Legacy: Subdomain stand im RecordName-Override, während Hostname nur die Zone war.
                // -> echten FQDN rekonstruieren und Zone setzen, damit Anzeige/Update stimmen.
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
