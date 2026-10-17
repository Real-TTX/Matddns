using Matddns.Models;
using Matddns.Services;

namespace Matddns.Tests;

public class ConfigServiceTests
{
    [Fact]
    public void Fresh_install_is_current_and_seeds_an_admin_user()
    {
        using var env = new TestEnvironment();

        var config = env.Config.Current;
        Assert.Equal(ConfigService.CurrentSchemaVersion, config.SchemaVersion);
        Assert.Equal("admin", config.Users.Single().Username);
        Assert.True(File.Exists(env.Paths.ConfigFile));
    }

    [Fact]
    public void Emptying_the_user_list_recreates_the_admin_account()
    {
        using var env = new TestEnvironment("""{ "schemaVersion": 2, "users": [] }""");

        Assert.Equal("admin", env.Config.Current.Users.Single().Username);
    }

    [Fact]
    public void Old_config_is_migrated_step_by_step()
    {
        using var env = new TestEnvironment("""
            {
              "rules": [ { "id": "r1", "trigger": "Interval", "validation": "Ping" },
                         { "id": "r2", "validation": "TcpPort" } ],
              "sources": [ { "id": "s1", "kind": "Push", "intervalSeconds": 60 } ],
              "domains": [ { "id": "d1", "entries": [ { "id": "e1", "hostname": "example.com", "recordName": "home" } ] } ]
            }
            """);

        var config = env.Config.Current;
        Assert.Equal(ConfigService.CurrentSchemaVersion, config.SchemaVersion);

        var intervalRule = config.Rules.Single(r => r.Id == "r1");
        Assert.False(intervalRule.OnChange);          // v0 -> v1: an old interval rule was not change-driven
        Assert.True(intervalRule.ValidatePing);       // v1 -> v2: single validation enum -> checkboxes
        Assert.Equal(RuleValidation.None, intervalRule.Validation);
        Assert.True(config.Rules.Single(r => r.Id == "r2").ValidateTcp);

        Assert.Equal(0, config.Sources.Single().IntervalSeconds); // push sources are never polled

        var entry = config.Domains.Single().Entries.Single();     // subdomain only in RecordName -> proper FQDN + zone
        Assert.Equal("home.example.com", entry.Hostname);
        Assert.Equal("example.com", entry.Domain);
    }

    [Fact]
    public void Legacy_single_account_becomes_a_regular_user_and_no_admin_is_seeded()
    {
        using var env = new TestEnvironment("""{ "schemaVersion": 2, "auth": { "username": "old", "passwordHash": "v1$AA==$AA==" } }""");

        var user = env.Config.Current.Users.Single();
        Assert.Equal("old", user.Username);
        Assert.Equal("v1$AA==$AA==", user.PasswordHash);
    }

    [Fact]
    public void Unreadable_config_is_preserved_instead_of_being_overwritten()
    {
        const string broken = "{ this is not json";
        using var env = new TestEnvironment(broken);

        var backup = Directory.GetFiles(env.DataDirectory, "config.json.corrupt-*").Single();
        Assert.Equal(broken, File.ReadAllText(backup));
        Assert.Equal("admin", env.Config.Current.Users.Single().Username);
        Assert.NotEqual(broken, File.ReadAllText(env.Paths.ConfigFile));
    }

    [Fact]
    public void Unknown_enum_values_fall_back_to_the_default_instead_of_failing()
    {
        using var env = new TestEnvironment("""
            { "schemaVersion": 2, "users": [ { "username": "u", "passwordHash": "x" } ],
              "sources": [ { "id": "s1", "kind": "FutureKind" } ] }
            """);

        Assert.Equal(SourceKind.PublicIp, env.Config.Current.Sources.Single().Kind);
        Assert.Empty(Directory.GetFiles(env.DataDirectory, "config.json.corrupt-*"));
    }

    [Fact]
    public void Mutations_are_persisted_and_survive_a_reload()
    {
        using var env = new TestEnvironment();
        env.Config.Mutate(c => c.Sources.Add(new SourceGroup { Id = "s1", Name = "Static", Kind = SourceKind.Static }));

        var reloaded = new ConfigService(env.Paths, env.Auth);
        reloaded.EnsureLoaded();

        Assert.Equal("Static", reloaded.Current.Sources.Single().Name);
        Assert.False(File.Exists(env.Paths.ConfigFile + ".tmp"));
    }

    [Fact]
    public void Current_returns_an_independent_snapshot()
    {
        using var env = new TestEnvironment();
        env.Config.Mutate(c => c.Sources.Add(new SourceGroup { Id = "s1", Name = "Original" }));

        var snapshot = env.Config.Current;
        snapshot.Sources.Single().Name = "Changed";
        snapshot.Sources.Clear();

        Assert.Equal("Original", env.Config.Current.Sources.Single().Name);
    }
}
