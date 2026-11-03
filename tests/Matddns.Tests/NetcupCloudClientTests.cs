using System.Net;
using System.Text;
using System.Text.Json;
using Matddns.Models;
using Matddns.Services;

namespace Matddns.Tests;

/// <summary>The new netcup CloudDNS client against a stand-in for api.netcup.com/v1: a single Bearer key, zones by
/// numeric id, records read from the active revision, and every change made in one content-matched changeset.</summary>
public class NetcupCloudClientTests
{
    private static readonly NetcupCloudSettings Settings = new() { ApiKey = "cloud-key" };

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Ok(object result, int nextPage = 0) =>
        Json(HttpStatusCode.OK, new { success = true, meta = new { pagination = new { nextPage } }, result });

    private static HttpResponseMessage Err(HttpStatusCode status, string code, string message) =>
        Json(status, new { success = false, errors = new[] { new { code, message } }, result = (object?)null });

    // A CloudDNS API with one existing "home A" record (rdata/ttl) or none; the zone exists and is on the new backend unless told otherwise.
    private static StubHandler Api(string? rdata, int? ttl = 300, bool zoneExists = true, bool managed = true,
        Func<RecordedCall, HttpResponseMessage?>? special = null) => new(call =>
    {
        if (special?.Invoke(call) is { } ans) return ans;
        var path = call.Uri.AbsolutePath;
        if (path == "/v1/domain")
            return zoneExists
                ? Ok(new object[] { new { id = 42, fqdn = "example.com", isDnsManaged = managed } })
                : Ok(Array.Empty<object>());
        if (path == "/v1/domain/42/dns/revision")
            return Ok(new object[]
            {
                new { identifier = "draft", state = "draft", serial = (int?)null },
                new { identifier = "live", state = "active", serial = (int?)1 },
            });
        if (path == "/v1/domain/42/dns/revision/live/record")
            return rdata == null
                ? Ok(Array.Empty<object>())
                : Ok(new object[] { new { identifier = "uuid-1", name = "home", type = "A", rdata, ttl = (int?)ttl, immutable = false } });
        if (call.Method == "POST" && path == "/v1/domain/42/changeset")
            return Ok(new { identifier = "new", state = "active", serial = 2 });
        return Err(HttpStatusCode.NotFound, "resourceDoesNotExist", "no route");
    });

    private static Task<(bool ok, string message)> Update(StubHandler h, string type = "A", string value = "203.0.113.5",
        string name = "home", string zone = "example.com", bool create = false) =>
        new NetcupCloudClient(new StubHttpClientFactory(h)).UpdateRecordAsync(Settings, zone, name, type, value, create, default);

    private static string[] Requests(StubHandler h) => h.Calls.Select(c => $"{c.Method} {c.Uri.AbsolutePath}").ToArray();

    [Fact]
    public async Task A_changed_address_is_written_in_one_changeset_with_the_bearer_key()
    {
        var handler = Api("203.0.113.1");

        var (ok, message) = await Update(handler);

        Assert.True(ok);
        Assert.Equal("updated home.example.com A -> 203.0.113.5", message);
        Assert.Equal([
            "GET /v1/domain",
            "GET /v1/domain/42/dns/revision",
            "GET /v1/domain/42/dns/revision/live/record",
            "POST /v1/domain/42/changeset",
        ], Requests(handler));
        Assert.All(handler.Calls, c => Assert.Equal("Bearer cloud-key", c.Authorization));

        var body = JsonDocument.Parse(handler.Calls[^1].Body).RootElement;
        Assert.True(body.GetProperty("autoCommit").GetBoolean());
        Assert.True(body.GetProperty("strict").GetBoolean());
        Assert.Equal("203.0.113.5", body.GetProperty("create")[0].GetProperty("rdata").GetString());
        Assert.Equal("203.0.113.1", body.GetProperty("delete")[0].GetProperty("rdata").GetString()); // the old one, matched by content
    }

    [Fact]
    public async Task An_unchanged_address_sends_no_changeset()
    {
        var handler = Api("203.0.113.5");

        var (ok, message) = await Update(handler);

        Assert.True(ok);
        Assert.StartsWith("unchanged", message);
        Assert.DoesNotContain(handler.Calls, c => c.Method == "POST");
    }

    [Fact]
    public async Task A_missing_record_is_refused_unless_creating_is_allowed()
    {
        var handler = Api(rdata: null);

        var (ok, message) = await Update(handler);

        Assert.False(ok);
        Assert.Equal("record does not exist", message);
        Assert.DoesNotContain(handler.Calls, c => c.Method == "POST");
    }

    [Fact]
    public async Task A_missing_record_is_created_with_no_delete_when_allowed()
    {
        var handler = Api(rdata: null);

        var (ok, message) = await Update(handler, create: true);

        Assert.True(ok);
        Assert.Equal("created home.example.com A -> 203.0.113.5", message);
        var body = JsonDocument.Parse(handler.Calls[^1].Body).RootElement;
        Assert.Equal("203.0.113.5", body.GetProperty("create")[0].GetProperty("rdata").GetString());
        Assert.False(body.TryGetProperty("delete", out _)); // nothing to delete on a create
    }

    [Fact]
    public async Task A_zone_the_key_cannot_see_is_reported_and_nothing_is_written()
    {
        var handler = Api(rdata: null, zoneExists: false);

        var (ok, message) = await Update(handler, create: true);

        Assert.False(ok);
        Assert.Contains("not found", message);
        Assert.DoesNotContain(handler.Calls, c => c.Method == "POST");
    }

    [Fact]
    public async Task A_zone_still_on_the_legacy_backend_points_to_netcup_legacy()
    {
        var handler = Api("203.0.113.1", managed: false);

        var (ok, message) = await Update(handler);

        Assert.False(ok);
        Assert.Contains("legacy", message);
        Assert.DoesNotContain(handler.Calls, c => c.Method == "POST");
    }

    [Fact]
    public async Task The_apex_is_addressed_with_an_at_sign()
    {
        var handler = new StubHandler(call =>
        {
            var path = call.Uri.AbsolutePath;
            if (path == "/v1/domain") return Ok(new object[] { new { id = 42, fqdn = "example.com", isDnsManaged = true } });
            if (path == "/v1/domain/42/dns/revision") return Ok(new object[] { new { identifier = "live", state = "active", serial = (int?)1 } });
            if (path.EndsWith("/record")) return Ok(new object[] { new { identifier = "u", name = "@", type = "A", rdata = "203.0.113.1", ttl = (int?)300, immutable = false } });
            return Ok(new { identifier = "new", state = "active", serial = 2 });
        });

        var (ok, message) = await Update(handler, name: "@");

        Assert.True(ok);
        Assert.Equal("updated example.com A -> 203.0.113.5", message);
        var body = JsonDocument.Parse(handler.Calls[^1].Body).RootElement;
        Assert.Equal("@", body.GetProperty("create")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_rejected_key_is_a_failure_that_does_not_leak_the_key()
    {
        var handler = new StubHandler(_ => Err(HttpStatusCode.BadRequest, "authenticationError", "bad key")); // netcup returns 400, not 401

        var (ok, message) = await Update(handler);

        Assert.False(ok);
        Assert.Contains("authenticationError", message);
        Assert.DoesNotContain("cloud-key", message);
    }

    [Fact]
    public async Task An_answer_that_is_not_json_is_a_clean_failure_that_does_not_leak_the_key()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>proxy</html>") });

        var (ok, message) = await Update(handler);

        Assert.False(ok);
        Assert.Contains("invalid response", message);
        Assert.DoesNotContain("cloud-key", message);
    }

    [Fact]
    public async Task Without_a_key_nothing_is_sent()
    {
        var handler = Api("203.0.113.1");

        var (ok, message) = await new NetcupCloudClient(new StubHttpClientFactory(handler))
            .UpdateRecordAsync(new NetcupCloudSettings(), "example.com", "home", "A", "203.0.113.5", true, default);

        Assert.False(ok);
        Assert.Equal("API key missing", message);
        Assert.Empty(handler.Calls);
    }
}
