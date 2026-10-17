using System.Net;
using System.Text;
using System.Text.Json;
using Matddns.Models;
using Matddns.Services;

namespace Matddns.Tests;

/// <summary>The Hetzner client against a stand-in for the DNS part of the Cloud API (RRSets addressed by zone, name and type; changes are actions).</summary>
public class HetznerClientTests
{
    private static readonly HetznerSettings Settings = new() { ApiToken = "cloud-token" };

    private const string ActionDone = """{"action":{"id":1,"status":"success"}}""";

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage NotFound() =>
        Json(HttpStatusCode.NotFound, """{"error":{"code":"not_found","message":"zone not found"}}""");

    private static string RrSet(string name, string type, int? ttl, params string[] values) =>
        JsonSerializer.Serialize(new { rrset = new { id = $"{name}/{type}", name, type, ttl, records = values.Select(v => new { value = v }) } });

    /// <summary>A Cloud API with one RRSet (or none) and a zone that exists (or not). Every change is accepted and done at once.</summary>
    private static StubHandler Api(string? rrset, bool zoneExists = true, Func<RecordedCall, HttpResponseMessage?>? special = null) => new(call =>
    {
        if (special?.Invoke(call) is { } answer) return answer;
        var path = call.Uri.AbsolutePath;
        if (call.Method == "POST") return Json(HttpStatusCode.Created, path.EndsWith("/rrsets") ? """{"rrset":{},"action":{"id":1,"status":"success"}}""" : ActionDone);
        if (path.Contains("/rrsets/")) return rrset == null ? NotFound() : Json(HttpStatusCode.OK, rrset);
        return zoneExists ? Json(HttpStatusCode.OK, """{"zone":{"id":1,"name":"example.com"}}""") : NotFound();
    });

    private static HetznerClient Client(StubHandler handler) => new(new StubHttpClientFactory(handler)) { ActionPollInterval = TimeSpan.Zero };

    private static Task<(bool ok, string message)> Update(StubHandler handler, string type = "A", string value = "203.0.113.5", string name = "home", string zone = "example.com", bool create = false) =>
        Client(handler).UpdateRecordAsync(Settings, zone, name, type, value, create, default);

    private static string[] Requests(StubHandler handler) => handler.Calls.Select(c => $"{c.Method} {c.Uri.AbsolutePath}").ToArray();

    [Fact]
    public async Task A_changed_address_overwrites_the_record_with_the_bearer_token()
    {
        var handler = Api(RrSet("home", "A", 60, "203.0.113.1"));

        var (ok, message) = await Update(handler);

        Assert.True(ok);
        Assert.Equal("updated home.example.com A -> 203.0.113.5", message);
        Assert.Equal(["GET /v1/zones/example.com/rrsets/home/A", "POST /v1/zones/example.com/rrsets/home/A/actions/set_records"], Requests(handler));
        Assert.All(handler.Calls, call => Assert.Equal("Bearer cloud-token", call.Authorization));
        Assert.Equal("""{"records":[{"value":"203.0.113.5"}]}""", handler.Calls[1].Body);
    }

    [Fact]
    public async Task The_address_of_the_record_is_left_alone_when_it_is_already_right()
    {
        var handler = Api(RrSet("home", "A", 60, "203.0.113.5"));

        var (ok, message) = await Update(handler);

        Assert.True(ok);
        Assert.StartsWith("unchanged", message);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task An_ipv6_address_in_another_notation_counts_as_the_same()
    {
        var handler = Api(RrSet("home", "AAAA", 60, "2001:0db8:0000:0000:0000:0000:0000:0001"));

        var (ok, message) = await Update(handler, "AAAA", "2001:db8::1");

        Assert.True(ok);
        Assert.StartsWith("unchanged", message);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task The_apex_is_addressed_with_an_at_sign()
    {
        var handler = Api(RrSet("@", "A", 60, "203.0.113.1"));

        var (ok, message) = await Update(handler, name: "@");

        Assert.True(ok);
        Assert.Equal("updated example.com A -> 203.0.113.5", message);
        Assert.Equal("GET /v1/zones/example.com/rrsets/@/A", Requests(handler)[0]);
    }

    [Theory]
    [InlineData("Home", "home")]
    [InlineData("home.example.com", "home")]   // a name that already carries the zone is shortened
    [InlineData("example.com", "@")]
    [InlineData("a.b", "a.b")]
    public async Task The_record_name_is_lower_case_and_relative_to_the_zone(string given, string sent)
    {
        var handler = Api(RrSet(sent, "A", 60, "203.0.113.5"));

        await Update(handler, name: given, zone: "Example.com.");

        Assert.Equal($"GET /v1/zones/example.com/rrsets/{sent}/A", Requests(handler)[0]);
    }

    [Fact]
    public async Task A_missing_record_is_refused_unless_creating_is_allowed()
    {
        var handler = Api(rrset: null);

        var (ok, message) = await Update(handler);

        Assert.False(ok);
        Assert.Equal("record does not exist", message);
        Assert.DoesNotContain(handler.Calls, c => c.Method == "POST");
    }

    [Fact]
    public async Task A_missing_record_is_created_with_a_short_ttl_when_allowed()
    {
        var handler = Api(rrset: null);

        var (ok, message) = await Update(handler, create: true);

        Assert.True(ok);
        Assert.Equal("created home.example.com A -> 203.0.113.5", message);
        Assert.Equal(["GET /v1/zones/example.com/rrsets/home/A", "GET /v1/zones/example.com", "POST /v1/zones/example.com/rrsets"], Requests(handler));
        Assert.Equal("""{"name":"home","type":"A","ttl":60,"records":[{"value":"203.0.113.5"}]}""", handler.Calls[2].Body);
    }

    [Fact]
    public async Task A_zone_the_token_cannot_see_is_reported_as_such_and_nothing_is_created()
    {
        var handler = Api(rrset: null, zoneExists: false);

        var (ok, message) = await Update(handler, create: true);

        Assert.False(ok);
        Assert.Contains("zone 'example.com' not found", message);
        Assert.DoesNotContain(handler.Calls, c => c.Method == "POST");
    }

    [Fact]
    public async Task A_cname_target_is_written_as_an_absolute_name_and_compared_without_the_final_dot()
    {
        var changed = Api(RrSet("www", "CNAME", 60, "old.example.net."));
        var (ok, _) = await Update(changed, "CNAME", "target.example.net", "www");
        Assert.True(ok);
        Assert.Equal("""{"records":[{"value":"target.example.net."}]}""", changed.Calls[1].Body);

        var same = Api(RrSet("www", "CNAME", 60, "Target.Example.Net."));
        var (sameOk, sameMessage) = await Update(same, "CNAME", "target.example.net", "www");
        Assert.True(sameOk);
        Assert.StartsWith("unchanged", sameMessage);
    }

    [Fact]
    public async Task The_comment_of_the_record_survives_an_update()
    {
        var rrset = """{"rrset":{"name":"home","type":"A","ttl":60,"records":[{"value":"203.0.113.1","comment":"set by hand"}]}}""";
        var handler = Api(rrset);

        await Update(handler);

        Assert.Equal("""{"records":[{"value":"203.0.113.5","comment":"set by hand"}]}""", handler.Calls[1].Body);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(null)]
    public async Task A_longer_ttl_is_shortened_after_the_update(int? ttl)
    {
        var handler = Api(RrSet("home", "A", ttl, "203.0.113.1"));

        var (ok, message) = await Update(handler);

        Assert.True(ok);
        Assert.DoesNotContain("TTL", message);
        Assert.Equal("POST /v1/zones/example.com/rrsets/home/A/actions/change_ttl", Requests(handler)[^1]);
        Assert.Equal("""{"ttl":60}""", handler.Calls[^1].Body);
    }

    [Fact]
    public async Task A_ttl_that_is_already_short_is_not_touched()
    {
        var handler = Api(RrSet("home", "A", 60, "203.0.113.1"));

        await Update(handler);

        Assert.DoesNotContain(Requests(handler), r => r.EndsWith("change_ttl"));
    }

    [Fact]
    public async Task A_failed_ttl_change_does_not_fail_the_update()
    {
        var handler = Api(RrSet("home", "A", 300, "203.0.113.1"), special: call =>
            call.Uri.AbsolutePath.EndsWith("change_ttl") ? Json(HttpStatusCode.UnprocessableEntity, """{"error":{"code":"invalid_input","message":"ttl is too low"}}""") : null);

        var (ok, message) = await Update(handler);

        Assert.True(ok);
        Assert.Contains("TTL stays 300", message);
        Assert.Contains("ttl is too low", message);
    }

    [Fact]
    public async Task A_record_set_with_several_records_is_not_overwritten()
    {
        var handler = Api(RrSet("home", "A", 60, "203.0.113.1", "203.0.113.2"));

        var (ok, message) = await Update(handler);

        Assert.False(ok);
        Assert.Contains("2 records", message);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task An_action_that_is_still_running_is_waited_for()
    {
        var polls = 0;
        var handler = Api(RrSet("home", "A", 60, "203.0.113.1"), special: call =>
        {
            if (call.Method == "POST") return Json(HttpStatusCode.Created, """{"action":{"id":7,"status":"running"}}""");
            if (call.Uri.AbsolutePath == "/v1/actions/7")
                return Json(HttpStatusCode.OK, ++polls < 3 ? """{"action":{"id":7,"status":"running"}}""" : """{"action":{"id":7,"status":"success"}}""");
            return null;
        });

        var (ok, _) = await Update(handler);

        Assert.True(ok);
        Assert.Equal(3, polls);
    }

    [Fact]
    public async Task An_action_that_fails_is_a_failure_with_its_reason()
    {
        var handler = Api(RrSet("home", "A", 60, "203.0.113.1"), special: call =>
            call.Method == "POST" ? Json(HttpStatusCode.Created, """{"action":{"id":7,"status":"error","error":{"code":"action_failed","message":"zone is locked"}}}""") : null);

        var (ok, message) = await Update(handler);

        Assert.False(ok);
        Assert.Equal("zone is locked (action_failed)", message);
    }

    [Fact]
    public async Task An_action_that_never_finishes_is_not_reported_as_a_failure()
    {
        var handler = Api(RrSet("home", "A", 60, "203.0.113.1"), special: call =>
            call.Method == "POST" || call.Uri.AbsolutePath.StartsWith("/v1/actions/") ? Json(HttpStatusCode.OK, """{"action":{"id":7,"status":"running"}}""") : null);

        var (ok, _) = await Update(handler);

        Assert.True(ok);
    }

    [Fact]
    public async Task A_rejected_token_says_which_kind_of_token_is_needed()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Unauthorized, """{"error":{"code":"unauthorized","message":"unable to authenticate"}}"""));

        var (ok, message) = await Update(handler);

        Assert.False(ok);
        Assert.Contains("unable to authenticate (unauthorized)", message);
        Assert.Contains("old DNS Console", message);
    }

    [Fact]
    public async Task A_read_only_token_is_reported_when_the_write_is_refused()
    {
        var handler = Api(RrSet("home", "A", 60, "203.0.113.1"), special: call =>
            call.Method == "POST" ? Json(HttpStatusCode.Forbidden, """{"error":{"code":"forbidden","message":"insufficient permissions"}}""") : null);

        var (ok, message) = await Update(handler);

        Assert.False(ok);
        Assert.Contains("read & write", message);
    }

    [Fact]
    public async Task An_answer_that_is_not_json_is_a_failure_that_does_not_leak_the_token()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>proxy</html>") });

        var (ok, message) = await Update(handler);

        Assert.False(ok);
        Assert.Contains("unexpected response", message);
        Assert.DoesNotContain("cloud-token", message);
    }

    [Fact]
    public async Task Without_a_token_nothing_is_sent()
    {
        var handler = Api(rrset: null);

        var (ok, message) = await new HetznerClient(new StubHttpClientFactory(handler)).UpdateRecordAsync(new HetznerSettings(), "example.com", "home", "A", "203.0.113.5", true, default);

        Assert.False(ok);
        Assert.Equal("API token missing", message);
        Assert.Empty(handler.Calls);
    }
}
