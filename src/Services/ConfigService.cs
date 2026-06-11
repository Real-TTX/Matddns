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

    /// <summary>Latest config schema version. Bump this and append a step to <see cref="Migrations"/> when the data shape changes.</summary>
    public const int CurrentSchemaVersion = 2;

    public void EnsureLoaded()
    {
        lock (_lock)
        {
            var loaded = false;
            if (File.Exists(_paths.ConfigFile))
            {
                try
                {
                    var json = File.ReadAllText(_paths.ConfigFile);
                    var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts);
                    if (cfg != null) { _config = cfg; loaded = true; }
                }
                catch { /* corrupt — keep defaults */ }
            }

            // A brand-new config is born at the current schema (nothing to migrate). An existing config
            // written before schema versioning deserializes to SchemaVersion 0 and is migrated up.
            if (!loaded) _config.SchemaVersion = CurrentSchemaVersion;

            var changed = RunMigrations(_config);

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

    // Migrations[i] upgrades a config from schema version i to i+1. Append-only — never reorder or remove.
    private static readonly Action<AppConfig>[] Migrations =
    {
        MigrateV0ToV1,
        MigrateV1ToV2,
    };

    /// <summary>
    /// Applies every schema migration between the config's stored version and <see cref="CurrentSchemaVersion"/>,
    /// in order, then stamps the new version. Returns true if anything was applied.
    /// </summary>
    private static bool RunMigrations(AppConfig cfg)
    {
        if (cfg.SchemaVersion < 0) cfg.SchemaVersion = 0;
        if (cfg.SchemaVersion >= CurrentSchemaVersion) return false; // already current (or newer, e.g. after a downgrade)
        for (var from = cfg.SchemaVersion; from < CurrentSchemaVersion; from++)
            Migrations[from](cfg);
        cfg.SchemaVersion = CurrentSchemaVersion;
        return true;
    }

    /// <summary>v0 → v1: pre-versioning data fixes (FQDN/zone split, push interval reset, independent rule triggers).</summary>
    private static void MigrateV0ToV1(AppConfig cfg)
    {
        foreach (var g in cfg.Domains)
            foreach (var e in g.Entries)
            {
                // the subdomain used to live in the RecordName override while Hostname held only the zone
                if (string.IsNullOrWhiteSpace(e.Domain)
                    && !string.IsNullOrWhiteSpace(e.RecordName)
                    && e.RecordName is not ("@" or "*")
                    && !string.IsNullOrWhiteSpace(e.Hostname)
                    && !e.Hostname.Equals(e.RecordName, StringComparison.OrdinalIgnoreCase)
                    && !e.Hostname.StartsWith(e.RecordName + ".", StringComparison.OrdinalIgnoreCase))
                {
                    e.Domain = e.Hostname;
                    e.Hostname = $"{e.RecordName}.{e.Hostname}";
                }
            }

        // Push sources are push-driven (never polled): a leftover interval is meaningless
        foreach (var s in cfg.Sources)
            if (s.Kind == SourceKind.Push) s.IntervalSeconds = 0;

        // legacy single trigger -> independent OnChange/Interval; an old "Interval" rule wasn't change-driven
        foreach (var r in cfg.Rules)
            if (r.Trigger == RuleTrigger.Interval) { r.OnChange = false; r.Trigger = RuleTrigger.OnChange; }
    }

    /// <summary>v1 → v2: single Validation enum -> independent ValidatePing / ValidateTcp checkboxes.</summary>
    private static void MigrateV1ToV2(AppConfig cfg)
    {
        foreach (var r in cfg.Rules)
        {
            if (r.Validation == RuleValidation.Ping) r.ValidatePing = true;
            else if (r.Validation == RuleValidation.TcpPort) r.ValidateTcp = true;
            r.Validation = RuleValidation.None; // neutralize so this only applies once
        }
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
