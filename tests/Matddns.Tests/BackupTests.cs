using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Matddns.Models;
using Matddns.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Matddns.Tests;

public class BackupTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    public void Dispose() => _env.Dispose();

    private void Fill()
    {
        _env.Config.Mutate(c =>
        {
            var src = new SourceGroup { Name = "Office", Kind = SourceKind.Static, Static = new StaticSettings { Ip = "203.0.113.5" }, Entries = { new SourceEntry { Label = "Static IP", CurrentIp = "203.0.113.5" } } };
            var entry = new DomainEntry { Hostname = "home.example.com" };
            var dom = new DomainGroup { Name = "CF", Kind = DomainKind.Cloudflare, Cloudflare = new CloudflareSettings { ApiToken = "cf-token" }, Entries = { entry } };
            c.Sources.Add(src);
            c.Domains.Add(dom);
            c.Rules.Add(new Rule { DomainGroupId = dom.Id, DomainEntryId = entry.Id, SourceEntryIdsInOrder = { src.Entries[0].Id }, LastRun = DateTime.UtcNow, LastResult = "err: x", FailCount = 4 });
            c.Settings.TimeZone = "Europe/Berlin";
        });
    }

    [Fact]
    public void An_export_has_the_configuration_and_its_credentials_but_no_accounts()
    {
        Fill();
        var json = _env.Config.ExportJson();
        var root = JsonDocument.Parse(json).RootElement;

        Assert.Equal("cf-token", root.GetProperty("domains")[0].GetProperty("cloudflare").GetProperty("apiToken").GetString());
        Assert.Equal(0, root.GetProperty("users").GetArrayLength());
        Assert.DoesNotContain(_env.Config.Read(c => c.Users[0].PasswordHash), json);
    }

    [Fact]
    public void An_import_replaces_the_configuration_keeps_the_accounts_and_runs_every_rule_again()
    {
        Fill();
        var export = _env.Config.ExportJson();
        var users = _env.Config.Read(c => c.Users.Select(u => u.Username).ToList());
        _env.Config.Mutate(c => { c.Sources.Clear(); c.Domains.Clear(); c.Rules.Clear(); c.Settings.TimeZone = ""; });

        var result = _env.Config.Import(export);

        Assert.Equal(new ConfigService.ImportResult(1, 1, 1), result);
        var cfg = _env.Config.Current;
        Assert.Equal(users, cfg.Users.Select(u => u.Username).ToList());
        Assert.Equal("Europe/Berlin", cfg.Settings.TimeZone);
        Assert.Equal((null, null, 0), (cfg.Rules[0].LastRun, cfg.Rules[0].LastResult, cfg.Rules[0].FailCount));
        Assert.Equal("cf-token", cfg.Domains[0].Cloudflare!.ApiToken);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"foo\": 1}")]
    [InlineData("{\"schemaVersion\": 99, \"sources\": []}")]
    public void A_file_that_is_no_configuration_changes_nothing(string json)
    {
        Fill();
        var before = _env.Config.ExportJson();

        Assert.Throws<FormatException>(() => _env.Config.Import(json));
        Assert.Equal(before, _env.Config.ExportJson());
    }

    [Fact]
    public void An_old_config_json_is_migrated_on_import()
    {
        // schema 1: one validation value instead of two checks
        var old = """{"schemaVersion":1,"sources":[],"domains":[],"rules":[{"id":"r1","validation":"Ping","domainGroupId":"g","domainEntryId":"e"}]}""";

        _env.Config.Import(old);

        Assert.True(_env.Config.Read(c => c.Rules.Single().ValidatePing));
    }
}

[Collection("app")]
public class BackupPageTests
{
    private readonly AppFactory _app;
    public BackupPageTests(AppFactory app) => _app = app;

    [Fact]
    public async Task The_page_downloads_and_imports_the_configuration()
    {
        var admin = await _app.AdminAsync();
        var config = _app.Services.GetRequiredService<ConfigService>();

        var export = await admin.GetAsync("/System?handler=Export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("application/json", export.Content.Headers.ContentType!.MediaType);
        var json = await export.Content.ReadAsStringAsync();

        var page = await (await admin.GetAsync("/System?tab=backup")).Content.ReadAsStringAsync();
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        using var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent(json), "file", "matddns.json" },
        };
        var import = await admin.PostAsync("/System?handler=Import", form);

        Assert.Equal(HttpStatusCode.Redirect, import.StatusCode);
        Assert.Equal("/System?tab=backup", import.Headers.Location!.ToString());
        var before = JsonDocument.Parse(json).RootElement;
        Assert.Equal(before.GetProperty("sources").GetArrayLength(), config.Read(c => c.Sources.Count));
        Assert.Equal(before.GetProperty("rules").GetArrayLength(), config.Read(c => c.Rules.Count));
    }

    [Fact]
    public async Task Without_sign_in_there_is_no_export()
    {
        var response = await _app.NewClient().GetAsync("/System?handler=Export");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Login", response.Headers.Location!.ToString());
    }
}
