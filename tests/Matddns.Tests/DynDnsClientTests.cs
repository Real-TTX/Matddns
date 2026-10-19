using Matddns.Models;
using Matddns.Services;

namespace Matddns.Tests;

public class DynDnsClientTests
{
    private static async Task<(bool Ok, RecordedCall Call)> UpdateAsync(DynDnsSettings settings, string ip, string responseBody)
    {
        var handler = StubHandler.Text(responseBody);
        var (ok, _) = await new DynDnsClient(new StubHttpClientFactory(handler)).UpdateAsync(settings, "home.example.com", ip, default);
        return (ok, handler.Calls.Single());
    }

    [Fact]
    public async Task Fills_placeholders_url_encoded_and_drops_the_unused_address_family()
    {
        var settings = new DynDnsSettings
        {
            UpdateUrl = "https://dyn.example/update?hostname={hostname}&ipv4={ipv4}&ipv6={ipv6}&pw={password}",
            Username = "user",
            Password = "p@ss word"
        };

        var (ok, call) = await UpdateAsync(settings, "203.0.113.5", "good 203.0.113.5");

        Assert.True(ok);
        Assert.Equal("https://dyn.example/update?hostname=home.example.com&ipv4=203.0.113.5&pw=p%40ss%20word", call.Uri.OriginalString);
    }

    [Fact]
    public async Task Sends_an_ipv6_value_only_in_the_ipv6_placeholder()
    {
        var settings = new DynDnsSettings { UpdateUrl = "https://dyn.example/u?v4={ipv4}&v6={ipv6}" };

        var (_, call) = await UpdateAsync(settings, "2001:db8::1", "good");

        Assert.Equal("https://dyn.example/u?v6=2001%3Adb8%3A%3A1", call.Uri.OriginalString);
    }

    [Fact]
    public async Task Authenticates_with_basic_auth_when_a_username_is_configured()
    {
        var settings = new DynDnsSettings { UpdateUrl = "https://dyn.example/u?ip={ip}", Username = "user", Password = "secret" };

        var (_, call) = await UpdateAsync(settings, "203.0.113.5", "good");

        Assert.Equal("Basic dXNlcjpzZWNyZXQ=", call.Authorization);
    }

    [Theory]
    [InlineData("good 203.0.113.5", true)]
    [InlineData("nochg 203.0.113.5", true)]
    [InlineData("""{"status":"ok","changed":true}""", true)]
    [InlineData("badauth", false)]
    [InlineData("nohost", false)]
    [InlineData("911", false)]
    [InlineData("  abuse  ", false)]
    public async Task Interprets_the_dyndns2_status_word(string body, bool expectedOk)
    {
        var (ok, _) = await UpdateAsync(new DynDnsSettings { UpdateUrl = "https://dyn.example/u" }, "203.0.113.5", body);

        Assert.Equal(expectedOk, ok);
    }

    [Fact]
    public async Task A_status_word_inside_the_echoed_address_is_not_a_failure()
    {
        var (ok, _) = await UpdateAsync(new DynDnsSettings { UpdateUrl = "https://dyn.example/u" }, "2001:db8::911", "good 2001:db8::911");

        Assert.True(ok);
    }

    [Fact]
    public async Task A_long_response_body_is_shortened_before_it_is_reported()
    {
        var handler = StubHandler.Text("<html>" + new string('x', 5000) + "</html>");

        var (_, response) = await new DynDnsClient(new StubHttpClientFactory(handler))
            .UpdateAsync(new DynDnsSettings { UpdateUrl = "https://dyn.example/u" }, "home.example.com", "203.0.113.5", default);

        Assert.InRange(response.Length, 1, 201);
    }

    [Fact]
    public async Task A_http_error_status_is_a_failure()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError) { Content = new StringContent("good") });

        var (ok, _) = await new DynDnsClient(new StubHttpClientFactory(handler))
            .UpdateAsync(new DynDnsSettings { UpdateUrl = "https://dyn.example/u" }, "home.example.com", "203.0.113.5", default);

        Assert.False(ok);
    }

    [Fact]
    public async Task Host_and_domain_give_the_name_and_the_zone_apart()
    {
        var settings = new DynDnsSettings { UpdateUrl = "https://dyn.example/update?host={host}&domain={domain}&ip={ipv4}" };

        var (_, call) = await UpdateAsync(settings, "203.0.113.5", "<ErrCount>0</ErrCount>");

        Assert.Equal("https://dyn.example/update?host=home&domain=example.com&ip=203.0.113.5", call.Uri.OriginalString);
    }

    [Theory]
    [InlineData("<?xml version=\"1.0\"?><interface-response><ErrCount>0</ErrCount></interface-response>", true)]
    [InlineData("<?xml version=\"1.0\"?><interface-response><ErrCount>1</ErrCount><errors><Err1>Passwords do not match</Err1></errors></interface-response>", false)]
    public async Task A_namecheap_error_count_is_a_failure(string body, bool ok)
        => Assert.Equal(ok, (await UpdateAsync(new DynDnsSettings { UpdateUrl = "https://dyn.example/u?ip={ipv4}" }, "203.0.113.5", body)).Ok);

    [Fact]
    public void Every_preset_is_an_http_url_with_a_unique_key()
    {
        Assert.All(Matddns.Support.DynDnsPresets.All, p => Assert.True(InputCheck.IsHttpUrl(p.Url), p.Key));
        Assert.Equal(Matddns.Support.DynDnsPresets.All.Count, Matddns.Support.DynDnsPresets.All.Select(p => p.Key).Distinct().Count());
    }
}

public class NetcupClientTests
{
    [Theory]
    [InlineData("home.example.com", "example.com", "home")]
    [InlineData("a.b.example.com", "example.com", "a.b")]
    [InlineData("example.com", "example.com", "@")]
    public void SplitHostname_separates_zone_and_record(string fqdn, string zone, string record) =>
        Assert.Equal((zone, record), NetcupClient.SplitHostname(fqdn));
}
