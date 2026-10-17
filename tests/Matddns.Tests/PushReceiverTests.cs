using System.Text;
using Matddns.Models;
using Matddns.Services;

namespace Matddns.Tests;

public class PushReceiverTests : IDisposable
{
    private const string Token = "0123456789abcdef";

    private readonly TestEnvironment _env = new();
    private readonly StubHandler _netcupApi = NetcupStub.EmptyZone();
    private readonly PushReceiver _receiver;
    private readonly SourceGroup _source = new() { Name = "Router", Kind = SourceKind.Push, Push = new PushSettings { Token = Token } };

    public PushReceiverTests()
    {
        _env.Config.Mutate(c => c.Sources.Add(_source));
        _receiver = new PushReceiver(_env.Config, _env.Log, new NetcupClient(new StubHttpClientFactory(_netcupApi)));
    }

    public void Dispose() => _env.Dispose();

    private SourceEntry StoredEntry() => _env.Config.Current.Sources.Single().Entries.Single();

    [Theory]
    [InlineData("abc", "abc", true)]
    [InlineData("abc", "abd", false)]
    [InlineData("abc", "", false)]
    [InlineData("", "", false)]
    [InlineData(null, "abc", false)]
    [InlineData("abc", null, false)]
    public void TokenEquals(string? stored, string? provided, bool expected) =>
        Assert.Equal(expected, PushReceiver.TokenEquals(stored, provided));

    [Fact]
    public void BasicAuthPassword_returns_the_password_part()
    {
        static string Header(string credentials) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));

        Assert.Equal("secret", PushReceiver.BasicAuthPassword(Header("user:secret")));
        Assert.Equal("tok", PushReceiver.BasicAuthPassword(Header(":tok")));
        Assert.Equal("a:b", PushReceiver.BasicAuthPassword(Header("user:a:b")));
        Assert.Null(PushReceiver.BasicAuthPassword("Bearer abc"));
        Assert.Null(PushReceiver.BasicAuthPassword("Basic !!!not-base64"));
        Assert.Null(PushReceiver.BasicAuthPassword(null));
    }

    [Theory]
    [InlineData("home.dyn.example.com", true)]
    [InlineData("a", true)]
    [InlineData("_acme-challenge.example.com", true)]
    [InlineData("a-b.example.com", true)]
    [InlineData("bad host.example.com", false)]
    [InlineData("-leading.example.com", false)]
    [InlineData("trailing-.example.com", false)]
    [InlineData("double..dot.example.com", false)]
    [InlineData(".leading-dot.com", false)]
    [InlineData("ünï.example.com", false)]
    [InlineData("a/b.example.com", false)]
    [InlineData("", false)]
    public void IsValidHostname(string host, bool expected) =>
        Assert.Equal(expected, PushReceiver.IsValidHostname(host));

    [Fact]
    public void IsValidHostname_enforces_label_and_total_length()
    {
        Assert.True(PushReceiver.IsValidHostname(new string('a', 63) + ".example.com"));
        Assert.False(PushReceiver.IsValidHostname(new string('a', 64) + ".example.com"));
        Assert.False(PushReceiver.IsValidHostname(string.Join('.', Enumerable.Repeat("abcdefgh", 32))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-token")]
    public async Task Unknown_token_is_unauthorized(string? token)
    {
        var result = await _receiver.UpdateAsync(token, null, "203.0.113.5", null, null, "198.51.100.1", default);

        Assert.Equal("unauthorized", result.Status);
        Assert.Empty(_env.Config.Current.Sources.Single().Entries);
    }

    [Fact]
    public async Task Stores_each_family_and_reports_changes()
    {
        var first = await _receiver.UpdateAsync(Token, null, "203.0.113.5", "2001:db8::5", null, null, default);
        var repeat = await _receiver.UpdateAsync(Token, null, "203.0.113.5", "2001:db8::5", null, null, default);
        var v4Only = await _receiver.UpdateAsync(Token, null, "203.0.113.6", null, null, null, default);

        Assert.Equal(("ok", true), (first.Status, first.Changed));
        Assert.Equal(("ok", false), (repeat.Status, repeat.Changed));
        Assert.Equal(("ok", true), (v4Only.Status, v4Only.Changed));

        var entry = StoredEntry();
        Assert.Equal("203.0.113.6", entry.CurrentIp);
        Assert.Equal("2001:db8::5", entry.CurrentIpv6); // a v4-only push leaves the v6 value alone
    }

    [Fact]
    public async Task Routes_a_value_by_its_actual_address_family()
    {
        await _receiver.UpdateAsync(Token, null, "2001:db8::7", null, null, null, default);

        var entry = StoredEntry();
        Assert.Null(entry.CurrentIp);
        Assert.Equal("2001:db8::7", entry.CurrentIpv6);
    }

    [Fact]
    public async Task Falls_back_to_the_caller_address_and_unwraps_mapped_ipv4()
    {
        var result = await _receiver.UpdateAsync(Token, null, null, null, null, "::ffff:203.0.113.9", default);

        Assert.Equal("ok", result.Status);
        Assert.Equal("203.0.113.9", StoredEntry().CurrentIp);
    }

    [Fact]
    public async Task Reports_no_ip_when_nothing_usable_was_sent()
    {
        var result = await _receiver.UpdateAsync(Token, null, "garbage", null, "also-garbage", null, default);

        Assert.Equal("no-ip", result.Status);
    }

    private void AddDynamicRule()
    {
        var zone = new DomainGroup { Name = "Netcup", Kind = DomainKind.Netcup, Netcup = new NetcupSettings { AllowDynamic = true } };
        _env.Config.Mutate(c =>
        {
            c.Domains.Add(zone);
            c.Rules.Add(new Rule
            {
                Dynamic = true,
                DynamicSourceId = _source.Id,
                DomainGroupId = zone.Id,
                DynamicZone = "example.com",
                DynamicPrefix = "dyn"
            });
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad host.dyn.example.com")]   // not a valid hostname
    [InlineData("other.example.com")]          // valid, but outside the dynamic namespace
    [InlineData("dyn.example.com.evil.net")]   // namespace as a mere substring
    public async Task Dynamic_receiver_rejects_unusable_hostnames_without_calling_the_dns_api(string hostname)
    {
        AddDynamicRule();

        var result = await _receiver.UpdateAsync(Token, hostname, "203.0.113.5", null, null, null, default);

        Assert.Equal("no-host", result.Status);
        Assert.Empty(_netcupApi.Calls);
    }

    [Fact]
    public async Task Dynamic_receiver_writes_the_record_relative_to_the_zone()
    {
        AddDynamicRule();

        var result = await _receiver.UpdateAsync(Token, "Home.DYN.example.com.", "203.0.113.5", null, null, null, default);

        Assert.Equal(("ok", true), (result.Status, result.Changed));
        var update = _netcupApi.Calls.Single(c => c.Body.Contains("updateDnsRecords"));
        Assert.Contains("\"domainname\":\"example.com\"", update.Body);
        Assert.Contains("\"hostname\":\"home.dyn\"", update.Body);
        Assert.Contains("\"destination\":\"203.0.113.5\"", update.Body);
    }
}
