using System.Text.Json;
using System.Text.Json.Serialization;
using Matddns.Models;

namespace Matddns.Services;

public class ConfigService
{
    private readonly PathOptions _paths;
    private readonly AuthService _auth;
    private readonly object _lock = new();
    private AppConfig _config = new();
    private bool _saveBlocked; // config.json exists but is unreadable and couldn't be backed up — don't clobber it
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new TolerantEnumConverterFactory() }
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
                catch (Exception ex)
                {
                    // The file exists but couldn't be read/parsed. NEVER overwrite it blindly with defaults —
                    // that would destroy the user's data. Preserve a copy first; only self-heal if that worked.
                    _config = new AppConfig();
                    if (TryPreserveCorruptConfig(out var backup))
                    {
                        Console.Error.WriteLine($"[Matddns] config.json is unreadable ({ex.Message}). " +
                            $"Preserved the original as '{Path.GetFileName(backup)}'; starting with a fresh config.");
                    }
                    else
                    {
                        // couldn't even back it up (e.g. transient I/O) — refuse to overwrite; run until fixed
                        _saveBlocked = true;
                        _config.SchemaVersion = CurrentSchemaVersion;
                        Console.Error.WriteLine($"[Matddns] config.json is unreadable ({ex.Message}) and could not be " +
                            "backed up. Refusing to overwrite it - no changes are saved until the file is fixed or removed.");
                        return;
                    }
                }
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

            // seed the initial admin account if there are no users at all. The password comes from
            // MATDDNS_ADMIN_PASSWORD, or is randomly generated and printed once so it isn't a guessable default.
            if (_config.Users.Count == 0)
            {
                var envPw = Environment.GetEnvironmentVariable("MATDDNS_ADMIN_PASSWORD");
                var generated = string.IsNullOrEmpty(envPw);
                var pw = generated ? GenerateInitialPassword() : envPw!;
                _config.Users.Add(new UserAccount { Username = "admin", PasswordHash = _auth.Hash(pw) });
                changed = true;
                if (generated)
                    Console.Error.WriteLine($"[Matddns] Created initial admin account - username 'admin', password '{pw}'. " +
                        "Log in and change it. Set MATDDNS_ADMIN_PASSWORD to choose your own initial password instead.");
                else
                    Console.Error.WriteLine("[Matddns] Created initial admin account 'admin' from MATDDNS_ADMIN_PASSWORD.");
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
        if (_saveBlocked) return; // an unreadable config.json we couldn't back up — never clobber it
        var json = JsonSerializer.Serialize(_config, JsonOpts);
        var tmp = _paths.ConfigFile + ".tmp";
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true); // fsync so a crash/power-loss can't leave a truncated file behind
        }
        File.Move(tmp, _paths.ConfigFile, true);
    }

    private static string GenerateInitialPassword()
        => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    // Copy an unreadable config.json aside so its data can be recovered. Best effort — a failure here
    // (e.g. transient I/O) tells the caller to refuse overwriting rather than risk data loss.
    private bool TryPreserveCorruptConfig(out string backupPath)
    {
        backupPath = "";
        try
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
            backupPath = _paths.ConfigFile + ".corrupt-" + stamp;
            File.Copy(_paths.ConfigFile, backupPath, overwrite: true);
            return true;
        }
        catch { return false; }
    }

    private static AppConfig CloneFrom(AppConfig src)
    {
        var json = JsonSerializer.Serialize(src, JsonOpts);
        return JsonSerializer.Deserialize<AppConfig>(json, JsonOpts)!;
    }

    // Deserialize enums leniently: an unknown member name (e.g. one added by a newer build, then downgraded)
    // maps to the enum's default instead of throwing — which would otherwise take the entire config down.
    // Serialization stays identical to JsonStringEnumConverter (writes the member name).
    private sealed class TolerantEnumConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type t) => t.IsEnum;
        public override JsonConverter CreateConverter(Type t, JsonSerializerOptions options)
            => (JsonConverter)Activator.CreateInstance(typeof(TolerantEnumConverter<>).MakeGenericType(t))!;
    }

    private sealed class TolerantEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
                return Enum.TryParse<T>(reader.GetString(), ignoreCase: true, out var v) ? v : default;
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var n) && Enum.IsDefined(typeof(T), n))
                return (T)Enum.ToObject(typeof(T), n);
            reader.Skip();
            return default;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());
    }
}
