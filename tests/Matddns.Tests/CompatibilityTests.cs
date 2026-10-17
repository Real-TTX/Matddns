using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Matddns.Models;
using Matddns.Platform;
using MatPlatform;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Matddns.Tests;

/// <summary>
/// What monitors knew before the platform contract keeps working: <c>/api/health</c> carries every old field next to the contract's, the
/// anonymous <c>/api/state</c> is the old monitoring state. The HTTP status of <c>/api/health</c> follows the contract (degraded = 200), <c>?strict=1</c>
/// gives the old one (503 unless ok).
/// </summary>
public sealed class MonitoringCompatibilityTests(MatddnsFactory site) : IClassFixture<MatddnsFactory>
{
    [Fact]
    public async Task Health_is_anonymous_and_has_the_contract_and_the_old_fields()
    {
        Sample.Seed(site);
        var response = await site.Anonymous().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(["status", "app", "version", "time", "checks", "sources", "rules", "ipChanges"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal("matddns", root.GetProperty("app").GetString());
        Assert.True(DateTimeOffset.TryParse(root.GetProperty("time").GetString(), out _));
        Assert.Equal(["config", "sources", "rules"], root.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("name").GetString()));

        // the old fields, with the old names
        Assert.Equal(["total", "entries", "ok", "errors"], root.GetProperty("sources").EnumerateObject().Select(p => p.Name));
        Assert.Equal(4, root.GetProperty("sources").GetProperty("total").GetInt32());
        Assert.Equal(["total", "ok", "issues", "disabled"], root.GetProperty("rules").EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, root.GetProperty("rules").GetProperty("disabled").GetInt32());
        Assert.Equal(["total", "last24h", "last7d", "lastChange"], root.GetProperty("ipChanges").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Degraded_is_200_and_503_with_strict_as_before()
    {
        Sample.Seed(site, c => c.Sources[0].Entries[0].LastError = "timeout");

        var health = await site.Anonymous().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using (var doc = JsonDocument.Parse(await health.Content.ReadAsStringAsync()))
        {
            Assert.Equal("degraded", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal(1, doc.RootElement.GetProperty("sources").GetProperty("errors").GetInt32());
            Assert.Contains(doc.RootElement.GetProperty("checks").EnumerateArray(), c => c.GetProperty("name").GetString() == "sources" && c.GetProperty("status").GetString() == "degraded");
        }

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await site.Anonymous().GetAsync("/api/health?strict=1")).StatusCode);
        Sample.Seed(site);
        Assert.Equal(HttpStatusCode.OK, (await site.Anonymous().GetAsync("/api/health?strict=1")).StatusCode);
    }

    [Fact]
    public void An_unreadable_config_is_down()
    {
        var call = new MatPlatformCall(new DefaultHttpContext(), "en", "test");
        var checks = MatddnsPlatform.HealthChecks(call, new Services.StatusSnapshot(), saveBlocked: true);
        Assert.Contains(checks, c => c.Name == "config" && c.Status == "down");
        Assert.All(checks.Where(c => c.Name != "config"), c => Assert.Equal("ok", c.Status));
    }

    [Fact]
    public async Task The_anonymous_state_is_the_old_monitoring_state()
    {
        Sample.Seed(site, c => c.Rules[1].LastResult = "err: provider said no");
        var response = await site.Anonymous().GetAsync("/api/state");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(["status", "time", "sources", "rules", "ipChanges"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal("degraded", root.GetProperty("status").GetString());

        var source = root.GetProperty("sources")[0];
        Assert.Equal(["name", "kind", "intervalSeconds", "lastChecked", "entries"], source.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["label", "interface", "ip", "ipv6", "lastChecked", "error"], source.GetProperty("entries")[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal("198.51.100.7", source.GetProperty("entries")[0].GetProperty("ip").GetString());

        var rule = root.GetProperty("rules")[0];
        Assert.Equal(
            ["target", "type", "group", "enabled", "trigger", "lastResult", "lastValue", "lastChange", "lastRun", "ok", "issue", "sources"],
            rule.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["name", "ip", "ipv6", "active", "missing"], rule.GetProperty("sources")[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(["total", "last24h", "last7d", "lastChange", "recent"], root.GetProperty("ipChanges").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task With_a_key_the_state_is_the_contract_and_a_wrong_key_is_not_the_old_state()
    {
        using (var doc = JsonDocument.Parse(await site.Api().GetStringAsync("/api/state")))
            Assert.Equal(["time", "values"], doc.RootElement.EnumerateObject().Select(p => p.Name));

        Assert.Equal(HttpStatusCode.Unauthorized, (await site.Api("wrong").GetAsync("/api/state")).StatusCode);
        var bearer = site.Anonymous();
        bearer.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.GetAsync("/api/state")).StatusCode);
    }

    [Fact]
    public async Task A_basic_login_of_a_proxy_still_gets_the_old_state()
    {
        var monitor = site.Anonymous();
        monitor.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String("monitor:pw"u8.ToArray()));
        using var doc = JsonDocument.Parse(await monitor.GetStringAsync("/api/state"));
        Assert.Equal(["status", "time", "sources", "rules", "ipChanges"], doc.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Healthz_stays()
        => Assert.Equal("ok", await site.Anonymous().GetStringAsync("/healthz"));
}

/// <summary>The engine API of Mattiverse (<c>/api/v1</c>, key <c>MATDDNS_API_KEY</c>) answers exactly as before; the platform key opens nothing there.</summary>
public sealed class EngineApiTests(MatddnsFactory site) : IClassFixture<MatddnsFactory>
{
    [Fact]
    public async Task Status_has_its_shape()
    {
        site.Configure(c => { c.Sources.Clear(); c.Domains.Clear(); c.Rules.Clear(); });
        var response = await site.Api(MatddnsFactory.EngineKey).GetAsync("/api/v1/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "{\"ok\":true,\"app\":\"matddns\",\"version\":\"local\",\"apiVersion\":1,\"counts\":{\"sources\":0,\"domains\":0,\"rules\":0}}",
            await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    [InlineData(MatddnsFactory.PlatformKey)]
    public async Task Without_the_engine_key_it_is_401(string? key)
    {
        var client = key is null ? site.Anonymous() : site.Api(key);
        var response = await client.GetAsync("/api/v1/sources");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("{\"error\":\"Invalid or missing X-Api-Key header.\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Sources_are_upserted_listed_and_deleted_and_run_answers_ok()
    {
        var api = site.Api(MatddnsFactory.EngineKey);
        var body = new { name = "apitest-static", kind = "Static", @static = new { ip = "192.0.2.10" }, managedBy = "apitest" };
        var created = await api.PostAsJsonAsync("/api/v1/sources", body);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("id").GetString()!;
        Assert.Equal("apitest", doc.RootElement.GetProperty("managedBy").GetString());

        // idempotent: the same body again is the same source
        using var again = JsonDocument.Parse(await (await api.PostAsJsonAsync("/api/v1/sources", body)).Content.ReadAsStringAsync());
        Assert.Equal(id, again.RootElement.GetProperty("id").GetString());
        Assert.Contains(id, await api.GetStringAsync("/api/v1/sources"));

        Assert.Equal("{\"ok\":true}", await (await api.PostAsync("/api/v1/run", null)).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await api.DeleteAsync("/api/v1/sources/" + id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync("/api/v1/nope")).StatusCode);
    }

    [Fact]
    public async Task Domain_credentials_are_never_returned()
    {
        var api = site.Api(MatddnsFactory.EngineKey);
        var response = await api.PostAsJsonAsync("/api/v1/domains", new
        {
            name = "apitest-cf", kind = "Cloudflare", cloudflare = new { apiToken = Sample.CloudflareToken },
            entries = new[] { new { hostname = "x.example.com", type = "A", recordName = "x", domain = "example.com" } },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Sample.CloudflareToken, text);
        Assert.Contains("\"credentialsSet\":true", text);
    }
}

/// <summary>The peer endpoints keep their token: <c>/api/source</c>, <c>/api/update</c>, <c>/nic/update</c> and <c>/update</c>; neither key opens them.</summary>
public sealed class PeerEndpointTests(MatddnsFactory site) : IClassFixture<MatddnsFactory>
{
    [Theory]
    [InlineData("/api/source")]
    [InlineData("/api/update?ipv4=192.0.2.1")]
    public async Task Without_a_token_it_is_401_also_with_a_key(string path)
    {
        Sample.Seed(site);
        foreach (var client in new[] { site.Anonymous(), site.Api(), site.Api(MatddnsFactory.EngineKey) })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("{\"status\":\"unauthorized\",\"message\":\"Invalid or missing token.\"}", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task With_the_token_a_peer_pushes_and_pulls()
    {
        Sample.Seed(site);
        var client = site.Anonymous();

        using (var doc = JsonDocument.Parse(await client.GetStringAsync($"/api/update?token={Sample.PushToken}&ipv4=192.0.2.1")))
        {
            Assert.Equal(["status", "changed", "ipv4", "ipv6", "time"], doc.RootElement.EnumerateObject().Select(p => p.Name));
            Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal("192.0.2.1", doc.RootElement.GetProperty("ipv4").GetString());
        }

        using (var doc = JsonDocument.Parse(await client.GetStringAsync($"/api/source?token={Sample.PushToken}")))
        {
            Assert.Equal(["time", "sources"], doc.RootElement.EnumerateObject().Select(p => p.Name));
            Assert.Contains("192.0.2.1", doc.RootElement.GetProperty("sources").GetRawText());
            Assert.DoesNotContain(Sample.PushToken, doc.RootElement.GetRawText());
        }
    }

    [Fact]
    public async Task The_dyndns2_protocol_stays_plain_text()
    {
        Sample.Seed(site);
        var client = site.Anonymous();
        Assert.Equal("badauth", await client.GetStringAsync("/nic/update?myip=192.0.2.5"));
        Assert.Equal("good 192.0.2.5", await client.GetStringAsync($"/nic/update?token={Sample.PushToken}&myip=192.0.2.5"));
        Assert.Equal("nochg 192.0.2.5", await client.GetStringAsync($"/update?token={Sample.PushToken}&myip=192.0.2.5"));
    }
}
