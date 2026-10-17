using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Matddns.Models;
using Matddns.Platform;
using Matddns.Services;
using MatPlatform;
using Microsoft.Extensions.DependencyInjection;

namespace Matddns.Tests;

/// <summary>Sample data: two sources (the second is the fallback), a Cloudflare account with two records, a failing record, secrets everywhere.</summary>
internal static class Sample
{
    public const string CloudflareToken = "cf-secret-token-4711";
    public const string PushToken = "push-secret-token-0815";
    public const string FritzPassword = "fritz-secret-password";

    public static void Seed(MatddnsFactory site, Action<AppConfig>? also = null)
    {
        var changed = new DateTime(2026, 10, 15, 7, 30, 0, DateTimeKind.Utc);
        site.Configure(c =>
        {
            c.Sources.Clear();
            c.Domains.Clear();
            c.Rules.Clear();

            c.Sources.Add(new SourceGroup
            {
                Id = "home", Name = "Home", Kind = SourceKind.PublicIp,
                Entries = { new SourceEntry { Id = "e-home", Label = "Public IP", CurrentIp = "198.51.100.7", CurrentIpv6 = "2001:db8::7", LastChecked = changed } },
            });
            c.Sources.Add(new SourceGroup
            {
                Id = "backup", Name = "Backup", Kind = SourceKind.Static, Static = new StaticSettings { Ip = "203.0.113.9" },
                Entries = { new SourceEntry { Id = "e-backup", Label = "Static", CurrentIp = "203.0.113.9", LastChecked = changed } },
            });
            c.Sources.Add(new SourceGroup
            {
                Id = "push", Name = "Router", Kind = SourceKind.Push, IntervalSeconds = 0, Push = new PushSettings { Token = PushToken },
                Entries = { new SourceEntry { Id = "e-push", Label = "Router" } },
            });
            c.Sources.Add(new SourceGroup
            {
                Id = "fritz", Name = "FRITZ!Box", Kind = SourceKind.Fritzbox,
                Fritzbox = new FritzboxSettings { BaseUrl = "http://fritz.box:49000", Username = "fritz", Password = FritzPassword },
                Entries = { new SourceEntry { Id = "e-fritz", Label = "WAN" } },
            });

            c.Domains.Add(new DomainGroup
            {
                Id = "cf", Name = "Cloudflare", Kind = DomainKind.Cloudflare, Cloudflare = new CloudflareSettings { ApiToken = CloudflareToken },
                Entries =
                {
                    new DomainEntry { Id = "rec-a", Hostname = "a.example.com", Type = DnsRecordType.A },
                    new DomainEntry { Id = "rec-b", Hostname = "b.example.com", Type = DnsRecordType.A },
                    new DomainEntry { Id = "rec-c", Hostname = "c.example.com", Type = DnsRecordType.A },
                },
            });

            // a: written from the fallback (failover); b: written from the first choice; c: disabled (not counted)
            c.Rules.Add(new Rule
            {
                Id = "r-a", DomainGroupId = "cf", DomainEntryId = "rec-a", SourceEntryIdsInOrder = { "e-home", "e-backup" },
                LastUsedSourceId = "e-backup", LastValue = "203.0.113.9", LastResult = "ok: updated", LastChange = changed, LastRun = changed,
            });
            c.Rules.Add(new Rule
            {
                Id = "r-b", DomainGroupId = "cf", DomainEntryId = "rec-b", SourceEntryIdsInOrder = { "e-home", "e-backup" },
                LastUsedSourceId = "e-home", LastValue = "198.51.100.7", LastResult = "ok: unchanged", LastChange = changed.AddHours(-1), LastRun = changed,
            });
            c.Rules.Add(new Rule
            {
                Id = "r-c", Enabled = false, DomainGroupId = "cf", DomainEntryId = "rec-c", SourceEntryIdsInOrder = { "e-home" },
            });
            also?.Invoke(c);
        });
    }
}

/// <summary>The MatApiDefinition: valid in both languages against the MatUi sprite, served with an ETag, the app of the registry.</summary>
public sealed class PlatformDefinitionTests(MatddnsFactory site) : IClassFixture<MatddnsFactory>
{
    private static readonly string Sprite = Path.Combine(AppContext.BaseDirectory, "Fixtures", "matui-icons.svg");

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void The_description_is_valid(string language)
        => Assert.Empty(MatPlatformValidator.Validate(MatPlatformBuilder.Build(MatddnsPlatform.Describe, language), MatPlatformValidator.ReadSprite(Sprite)));

    [Fact]
    public async Task The_served_definition_is_valid_and_names_the_app()
    {
        var json = await site.Anonymous().GetStringAsync("/api");
        Assert.Empty(MatPlatformValidator.Validate(json, MatPlatformValidator.ReadSprite(Sprite)));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("contract").GetInt32());
        Assert.Equal("matddns", root.GetProperty("app").GetProperty("id").GetString());
        Assert.Equal("#204db6", root.GetProperty("app").GetProperty("accent").GetString());
        Assert.True(root.GetProperty("auth").GetProperty("apiKey").GetBoolean());
        Assert.False(root.GetProperty("auth").TryGetProperty("identity", out _));

        var tiles = root.GetProperty("state").EnumerateArray().Select(t => t.GetProperty("key").GetString()).ToList();
        Assert.Equal(["ipv4", "ipv6", "activeSource", "failovers", "records", "recordsInSync", "lastUpdate", "lastError"], tiles);
        var fields = root.GetProperty("configure").EnumerateArray().Select(f => f.GetProperty("key").GetString()).ToList();
        Assert.InRange(fields.Count, 1, 10);
        Assert.Contains(root.GetProperty("actions").EnumerateArray(), a => a.GetProperty("id").GetString() == "runNow" && a.GetProperty("kind").GetString() == "post");
        // Matddns has no roles: every account may do everything.
        Assert.False(root.TryGetProperty("roles", out var roles) && roles.GetArrayLength() > 0);
    }

    [Fact]
    public async Task The_texts_follow_accept_language()
    {
        var client = site.Anonymous();
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        Assert.Contains("\"Update now\"", await client.GetStringAsync("/api"));
        Assert.Contains("\"Jetzt aktualisieren\"", await site.Anonymous().GetStringAsync("/api"));
    }

    [Fact]
    public async Task The_definition_has_an_etag_and_answers_304()
    {
        var client = site.Anonymous();
        var first = await client.GetAsync("/api");
        var etag = first.Headers.ETag!;
        using var again = new HttpRequestMessage(HttpMethod.Get, "/api");
        again.Headers.IfNoneMatch.Add(etag);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(again)).StatusCode);
    }
}

/// <summary>The endpoints of the contract with the key: shapes, status codes, values, settings, actions, no secrets.</summary>
public sealed class PlatformApiTests(MatddnsFactory site) : IClassFixture<MatddnsFactory>
{
    [Theory]
    [InlineData("GET", "/api/state/ipv4")]
    [InlineData("GET", "/api/configure")]
    [InlineData("PUT", "/api/configure")]
    [InlineData("POST", "/api/actions/runNow")]
    public async Task Without_the_key_it_is_401(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "PUT") request.Content = JsonContent.Create(new { values = new { } });
        Assert.Equal(HttpStatusCode.Unauthorized, (await site.Anonymous().SendAsync(request)).StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/state")]
    [InlineData("GET", "/api/state/ipv4")]
    [InlineData("GET", "/api/configure")]
    [InlineData("POST", "/api/actions/runNow")]
    public async Task A_wrong_key_or_the_engine_key_is_401(string method, string path)
    {
        // One key per API: with MAT_API_KEY set, the engine key opens only the engine API.
        foreach (var key in new[] { "wrong", MatddnsFactory.EngineKey })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            Assert.Equal(HttpStatusCode.Unauthorized, (await site.Api(key).SendAsync(request)).StatusCode);
        }
    }

    [Fact]
    public async Task State_has_every_tile_with_the_values_of_matddns()
    {
        Sample.Seed(site, c => c.Rules.Add(new Rule
        {
            Id = "r-x", DomainGroupId = "cf", DomainEntryId = "rec-c", SourceEntryIdsInOrder = { "e-home" }, LastResult = "err: boom",
        }));
        site.Services.GetRequiredService<LogService>().Log(Services.LogLevel.Error, "rule:A c.example.com", "boom");

        var response = await site.Api().GetAsync("/api/state");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(["time", "values"], root.EnumerateObject().Select(p => p.Name));
        Assert.True(DateTimeOffset.TryParse(root.GetProperty("time").GetString(), out _));
        var values = root.GetProperty("values");

        foreach (var tile in MatPlatformBuilder.Build(MatddnsPlatform.Describe).State)
            Assert.True(values.TryGetProperty(tile.Key, out _), tile.Key);

        // one record each from home and backup (a tie: the first record decides), the failing one has never written
        Assert.Equal(1, values.GetProperty("failovers").GetProperty("value").GetInt32());
        Assert.Equal("warn", values.GetProperty("failovers").GetProperty("level").GetString());
        Assert.Equal(3, values.GetProperty("records").GetProperty("value").GetInt32());
        Assert.Equal(2, values.GetProperty("recordsInSync").GetProperty("value").GetInt32());
        Assert.Equal("2026-10-15T07:30:00+00:00", values.GetProperty("lastUpdate").GetProperty("value").GetString());
        Assert.Equal("2001:db8::7", values.GetProperty("ipv6").GetProperty("value").GetString());
        Assert.Equal("error", values.GetProperty("lastError").GetProperty("value").GetString());
        Assert.Contains("boom", values.GetProperty("lastError").GetProperty("text").GetString());
    }

    [Fact]
    public async Task The_active_source_is_the_one_most_records_are_written_from()
    {
        Sample.Seed(site, c => c.Rules.Single(r => r.Id == "r-b").LastUsedSourceId = "e-backup");

        using var doc = JsonDocument.Parse(await site.Api().GetStringAsync("/api/state"));
        var values = doc.RootElement.GetProperty("values");
        Assert.Equal("Backup · Static", values.GetProperty("activeSource").GetProperty("value").GetString());
        Assert.Equal("203.0.113.9", values.GetProperty("ipv4").GetProperty("value").GetString());
        // the fallback has no IPv6: the first source that has one
        Assert.Equal("2001:db8::7", values.GetProperty("ipv6").GetProperty("value").GetString());
        Assert.Equal(2, values.GetProperty("failovers").GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task One_state_value_by_key_and_404_for_an_unknown_key()
    {
        Sample.Seed(site);
        using var doc = JsonDocument.Parse(await site.Api().GetStringAsync("/api/state/records"));
        Assert.Equal("records", doc.RootElement.GetProperty("key").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("value").GetInt32());

        var unknown = await site.Api().GetAsync("/api/state/nope");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Contains("\"errors\"", await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Configure_reads_and_changes_the_settings_and_logs_who_did_it()
    {
        using (var doc = JsonDocument.Parse(await site.Api().GetStringAsync("/api/configure")))
        {
            var values = doc.RootElement.GetProperty("values");
            Assert.Equal(["timeZone", "anonymousDashboard", "minLogLevel", "logRetentionDays"], values.EnumerateObject().Select(p => p.Name));
            Assert.Equal("Info", values.GetProperty("minLogLevel").GetString());
        }

        var response = await site.Api().PutAsJsonAsync("/api/configure", new
        {
            values = new { timeZone = "Europe/Berlin", anonymousDashboard = true, minLogLevel = "Warn", logRetentionDays = 30 },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
            Assert.Equal("Europe/Berlin", doc.RootElement.GetProperty("values").GetProperty("timeZone").GetString());

        var settings = site.Read(c => c.Settings);
        Assert.Equal("Europe/Berlin", settings.TimeZone);
        Assert.True(settings.AnonymousDashboard);
        Assert.Equal(Services.LogLevel.Warn, settings.MinLogLevel);
        Assert.Equal(30, settings.LogRetentionDays);
        // the System page shows the same values: they are the same settings in config.json
        Assert.Contains("\"timeZone\": \"Europe/Berlin\"", File.ReadAllText(Path.Combine(site.DataDirectory, "config.json")));

        // only the keys that are sent change
        Assert.Equal(HttpStatusCode.OK, (await site.Api().PutAsJsonAsync("/api/configure", new { values = new { minLogLevel = "Info" } })).StatusCode);
        Assert.Equal("Europe/Berlin", site.Read(c => c.Settings.TimeZone));
        Assert.Contains(site.Services.GetRequiredService<LogService>().Recent(50), e => e.Source == "settings" && e.Message.Contains("api-key"));

        // back to the defaults for the other tests
        await site.Api().PutAsJsonAsync("/api/configure", new { values = new { timeZone = "", anonymousDashboard = false, logRetentionDays = 14 } });
    }

    [Theory]
    [InlineData("{\"values\":{\"timeZone\":\"Mars/Olympus\",\"logRetentionDays\":7}}", "timeZone")]
    [InlineData("{\"values\":{\"logRetentionDays\":-1,\"timeZone\":\"UTC\"}}", "logRetentionDays")]
    [InlineData("{\"values\":{\"logRetentionDays\":1.5}}", "logRetentionDays")]
    [InlineData("{\"values\":{\"minLogLevel\":\"Loud\"}}", "minLogLevel")]
    public async Task Configure_refuses_wrong_values_and_saves_nothing(string body, string key)
    {
        var before = site.Read(c => (c.Settings.TimeZone, c.Settings.LogRetentionDays, c.Settings.MinLogLevel));
        var response = await site.Api().PutAsync("/api/configure", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("errors").TryGetProperty(key, out _));
        Assert.Equal(before, site.Read(c => (c.Settings.TimeZone, c.Settings.LogRetentionDays, c.Settings.MinLogLevel)));
    }

    [Fact]
    public async Task Update_now_starts_a_pass()
    {
        var response = await site.Api().PostAsync("/api/actions/runNow", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("message").GetString()));
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("open")]
    [InlineData("openSystem")]
    public async Task An_unknown_action_or_a_link_is_404(string id)
        => Assert.Equal(HttpStatusCode.NotFound, (await site.Api().PostAsync("/api/actions/" + id, null)).StatusCode);

    [Fact]
    public async Task No_answer_contains_a_secret()
    {
        Sample.Seed(site);
        var hash = site.User("admin")!.PasswordHash;
        var answers = new List<string>
        {
            await site.Anonymous().GetStringAsync("/api"),
            await site.Anonymous().GetStringAsync("/api/health"),
            await site.Anonymous().GetStringAsync("/api/state"),
            await site.Api().GetStringAsync("/api/state"),
            await site.Api().GetStringAsync("/api/configure"),
            await site.Api().GetStringAsync("/api/state/lastError"),
        };

        foreach (var answer in answers)
        {
            foreach (var secret in new[] { Sample.CloudflareToken, Sample.PushToken, Sample.FritzPassword, hash, MatddnsFactory.PlatformKey, MatddnsFactory.EngineKey, site.DataDirectory })
                Assert.DoesNotContain(secret, answer);
        }
    }
}

/// <summary>Only <c>MATDDNS_API_KEY</c> set: the engine key opens the platform API as well (one key for the one caller).</summary>
public sealed class EngineKeyFallbackTests(EngineKeyOnlyFactory site) : IClassFixture<EngineKeyOnlyFactory>
{
    [Fact]
    public async Task The_engine_key_opens_the_platform_api()
    {
        Assert.Equal(HttpStatusCode.OK, (await site.Api(MatddnsFactory.EngineKey).GetAsync("/api/configure")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await site.Api(MatddnsFactory.PlatformKey).GetAsync("/api/configure")).StatusCode);
        using var doc = JsonDocument.Parse(await site.Anonymous().GetStringAsync("/api"));
        Assert.True(doc.RootElement.GetProperty("auth").GetProperty("apiKey").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await site.Api(MatddnsFactory.EngineKey).GetAsync("/api/v1/status")).StatusCode);
    }
}

/// <summary>No key at all: everything with values is 401 (the old anonymous monitoring stays), the engine API 503.</summary>
public sealed class NoKeyTests(NoKeyFactory site) : IClassFixture<NoKeyFactory>
{
    [Fact]
    public async Task Everything_with_values_is_401_and_the_definition_says_the_key_is_off()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await site.Api("anything").GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await site.Anonymous().GetAsync("/api/configure")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await site.Anonymous().GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await site.Anonymous().GetAsync("/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await site.Api("anything").GetAsync("/api/v1/status")).StatusCode);

        using var doc = JsonDocument.Parse(await site.Anonymous().GetStringAsync("/api"));
        Assert.False(doc.RootElement.GetProperty("auth").GetProperty("apiKey").GetBoolean());
    }
}
