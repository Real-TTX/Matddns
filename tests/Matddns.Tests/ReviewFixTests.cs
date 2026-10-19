using System.Net;
using System.Net.Http.Json;
using Matddns.Models;
using Matddns.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Matddns.Tests;

/// <summary>The checks the review added: only http(s) addresses are called, records are real DNS names, the System page validates like the platform API.</summary>
public class InputCheckTests
{
    [Theory]
    [InlineData("https://dyndns.example.com", true)]
    [InlineData("http://fritz.box:49000", true)]
    [InlineData("https://192.168.1.1", true)]
    [InlineData("https://example.org/nic/update?hostname={hostname}&myip={ipv4}", true)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("ftp://example.com", false)]
    [InlineData("gopher://127.0.0.1:6379/_", false)]
    [InlineData("/relative/path", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_absolute_http_urls_pass(string? url, bool ok) => Assert.Equal(ok, InputCheck.IsHttpUrl(url));

    [Theory]
    [InlineData("home.example.com", true)]
    [InlineData("Home.Example.com.", true)]
    [InlineData("_acme.example.com", true)]
    [InlineData("not a host", false)]
    [InlineData("-bad.example.com", false)]
    [InlineData("a..b", false)]
    [InlineData("", false)]
    public void Hostnames_are_dns_names(string host, bool ok) => Assert.Equal(ok, InputCheck.IsHostname(host));

    [Theory]
    [InlineData("A", "203.0.113.1", "2001:db8::1", "203.0.113.1")]
    [InlineData("AAAA", "203.0.113.1", "2001:db8::1", "2001:db8::1")]
    [InlineData("AAAA", "203.0.113.1", null, null)]
    [InlineData("CNAME", null, "2001:db8::1", "2001:db8::1")]
    public void The_address_of_a_record_follows_its_type(string type, string? v4, string? v6, string? expected)
        => Assert.Equal(expected, RecordAddress.For(type, v4, v6));
}

[Collection("app")]
public class ReviewFormTests
{
    private readonly AppFactory _app;
    public ReviewFormTests(AppFactory app) => _app = app;

    private ConfigService Config => _app.Services.GetRequiredService<ConfigService>();

    [Theory]
    [InlineData("Unifi", "UniUrl", "file:///etc/passwd")]
    [InlineData("Fritzbox", "FbUrl", "gopher://fritz.box")]
    [InlineData("Matddns", "MdUrl", "")]
    public async Task A_source_with_an_address_that_is_not_http_is_refused(string kind, string field, string url)
    {
        var admin = await _app.AdminAsync();
        var name = $"bad-url-{kind}";
        var response = await AppFactory.PostForm(admin, "/Sources/Edit?handler=Create", "/Sources/Edit", ("Name", name), ("Kind", kind), ("IntervalSeconds", "60"), (field, url));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Sources/Edit", response.Headers.Location!.ToString());
        Assert.False(Config.Read(c => c.Sources.Any(s => s.Name == name)));
    }

    [Fact]
    public async Task A_dyndns_group_needs_an_http_update_url()
    {
        var admin = await _app.AdminAsync();
        await AppFactory.PostForm(admin, "/Domains/Edit?handler=Create", "/Domains/Edit", ("Name", "bad-dyndns"), ("Kind", "DynDns"), ("DynUrl", "file:///x"));

        Assert.False(Config.Read(c => c.Domains.Any(d => d.Name == "bad-dyndns")));
    }

    [Fact]
    public async Task A_record_must_be_a_dns_name_and_is_added_once()
    {
        var group = new DomainGroup { Name = "records-check", Kind = DomainKind.DynDns, DynDns = new DynDnsSettings { UpdateUrl = "https://example.org/u?h={hostname}" } };
        Config.Mutate(c => c.Domains.Add(group));
        var admin = await _app.AdminAsync();
        var form = $"/Domains/Edit?id={group.Id}";

        await AppFactory.PostForm(admin, "/Domains/Edit?handler=AddEntry", form, ("Id", group.Id), ("Type", "A"), ("Hostname", "not a host"));
        await AppFactory.PostForm(admin, "/Domains/Edit?handler=AddEntry", form, ("Id", group.Id), ("Type", "A"), ("Hostname", "ok.example.com"));
        await AppFactory.PostForm(admin, "/Domains/Edit?handler=AddEntry", form, ("Id", group.Id), ("Type", "A"), ("Hostname", "OK.example.com"));

        var entries = Config.Read(c => c.Domains.Single(d => d.Id == group.Id).Entries.Select(e => e.Hostname).ToList());
        Assert.Equal(["ok.example.com"], entries);
    }

    [Fact]
    public async Task The_system_page_refuses_an_unknown_time_zone_and_keeps_the_old_one()
    {
        var before = Config.Read(c => c.Settings.TimeZone);
        var admin = await _app.AdminAsync();

        await AppFactory.PostForm(admin, "/System", "/System", ("MinLogLevel", "Info"), ("LogRetentionDays", "14"), ("TimeZone", "Mars/Olympus_Mons"));

        Assert.Equal(before, Config.Read(c => c.Settings.TimeZone));
    }
}

public sealed class ReviewApiTests(MatddnsFactory site) : IClassFixture<MatddnsFactory>
{
    [Fact]
    public async Task A_dyndns_update_url_that_is_not_http_is_400()
    {
        var api = site.Api(MatddnsFactory.EngineKey);
        var response = await api.PostAsJsonAsync("/api/v1/domains", new { name = "bad-url", kind = "DynDns", dynDns = new { updateUrl = "file:///etc/passwd" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("dynDns.updateUrl", await response.Content.ReadAsStringAsync());
    }
}
