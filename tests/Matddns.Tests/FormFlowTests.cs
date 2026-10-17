using System.Net;
using Matddns.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Matddns.Tests;

/// <summary>The forms of the pages as a browser sends them: every handler answers with a redirect to a page that exists (a 500 hides behind a created record otherwise).</summary>
[Collection("app")]
public class FormFlowTests
{
    private readonly AppFactory _app;

    public FormFlowTests(AppFactory app) => _app = app;

    private sealed class Flow
    {
        private readonly HttpClient _client;
        public Flow(HttpClient client) => _client = client;

        /// <summary>Posts a form, expects a redirect and that the page it points to renders; returns where it points.</summary>
        public async Task<string> Post(string handlerUrl, string formPage, params (string Key, string Value)[] fields)
        {
            var response = await AppFactory.PostForm(_client, handlerUrl, formPage, fields);
            Assert.True(response.StatusCode == HttpStatusCode.Redirect, $"POST {handlerUrl} answered {(int)response.StatusCode}");
            var location = response.Headers.Location!.ToString();
            var page = await _client.GetAsync(location);
            Assert.True(page.StatusCode == HttpStatusCode.OK, $"POST {handlerUrl} redirects to {location}, which answers {(int)page.StatusCode}");
            return location;
        }

        public static string IdOf(string location) => location[(location.IndexOf("id=", StringComparison.Ordinal) + 3)..];
    }

    [Fact]
    public async Task Source_forms_redirect_to_pages_that_exist()
    {
        var flow = new Flow(await _app.AdminAsync());

        var created = await flow.Post("/Sources/Edit?handler=Create", "/Sources/Edit", ("Name", "Flow source"), ("Kind", "Static"), ("IntervalSeconds", "60"), ("StaticIp", "203.0.113.50"));
        var id = Flow.IdOf(created);
        Assert.StartsWith("/Sources/Edit?id=", created);

        var form = $"/Sources/Edit?id={id}";
        Assert.StartsWith("/Sources/Edit", await flow.Post("/Sources/Edit?handler=Save", form, ("Id", id), ("Name", "Flow source renamed"), ("IntervalSeconds", "60"), ("StaticIp", "203.0.113.51")));
        Assert.StartsWith("/Sources/Edit", await flow.Post("/Sources/Edit?handler=Refresh", form, ("Id", id)));
        Assert.Equal("/Sources", await flow.Post("/Sources/Edit?handler=Delete", form, ("Id", id)));
    }

    [Fact]
    public async Task Source_form_with_a_missing_name_goes_back_to_the_form()
    {
        var flow = new Flow(await _app.AdminAsync());

        var location = await flow.Post("/Sources/Edit?handler=Create", "/Sources/Edit", ("Name", ""), ("Kind", "PublicIp"), ("IntervalSeconds", "60"));

        Assert.Equal("/Sources/Edit", location);
    }

    [Fact]
    public async Task Domain_forms_redirect_to_pages_that_exist()
    {
        var flow = new Flow(await _app.AdminAsync());

        var created = await flow.Post("/Domains/Edit?handler=Create", "/Domains/Edit", ("Name", "Flow domains"), ("Kind", "DynDns"), ("DynUrl", "https://example.org/update?h={hostname}"));
        var id = Flow.IdOf(created);
        var form = $"/Domains/Edit?id={id}";

        Assert.StartsWith("/Domains/Edit", await flow.Post("/Domains/Edit?handler=AddEntry", form, ("Id", id), ("Type", "A"), ("Hostname", "flow.example.com")));
        var entryId = _app.Services.GetRequiredService<ConfigService>().Read(c => c.Domains.First(d => d.Id == id).Entries.Single().Id);
        Assert.StartsWith("/Domains/Edit", await flow.Post("/Domains/Edit?handler=Save", form, ("Id", id), ("Name", "Flow domains renamed"), ("DynUrl", "https://example.org/update?h={hostname}")));
        Assert.StartsWith("/Domains/Edit", await flow.Post("/Domains/Edit?handler=DeleteEntry", form, ("Id", id), ("EntryId", entryId)));
        Assert.Equal("/Domains", await flow.Post("/Domains/Edit?handler=Delete", form, ("Id", id)));
    }

    [Fact]
    public async Task Rule_forms_redirect_to_pages_that_exist()
    {
        var flow = new Flow(await _app.AdminAsync());
        var config = _app.Services.GetRequiredService<ConfigService>();

        var sourceLocation = await flow.Post("/Sources/Edit?handler=Create", "/Sources/Edit", ("Name", "Rule flow source"), ("Kind", "Static"), ("IntervalSeconds", "60"), ("StaticIp", "203.0.113.60"));
        var domainLocation = await flow.Post("/Domains/Edit?handler=Create", "/Domains/Edit", ("Name", "Rule flow domain"), ("Kind", "DynDns"), ("DynUrl", "https://example.org/update"));
        var sourceId = Flow.IdOf(sourceLocation);
        var domainId = Flow.IdOf(domainLocation);
        await flow.Post("/Domains/Edit?handler=AddEntry", domainLocation, ("Id", domainId), ("Type", "A"), ("Hostname", "rule-flow.example.com"));
        var entryId = config.Read(c => c.Domains.First(d => d.Id == domainId).Entries.Single().Id);
        var sourceEntryId = config.Read(c => c.Sources.First(s => s.Id == sourceId).Entries.FirstOrDefault()?.Id) ?? "se-flow";

        var created = await flow.Post("/Rules/Edit?handler=Create", "/Rules/Edit", ("OnChange", "true"), ("IntervalSeconds", "300"), ("DomainEntryRef", $"{domainId}:{entryId}"),
            ("ValidationPort", "443"), ("SourceEntryIdsInOrder", sourceEntryId));
        var ruleId = Flow.IdOf(created);
        var form = $"/Rules/Edit?id={ruleId}";

        Assert.StartsWith("/Rules/Edit", await flow.Post("/Rules/Edit?handler=Save", form, ("Id", ruleId), ("OnChange", "true"), ("IntervalSeconds", "120"), ("Enabled", "true"),
            ("DomainEntryRef", $"{domainId}:{entryId}"), ("ValidateTcp", "true"), ("ValidationPort", "8443")));
        Assert.StartsWith("/Rules/Edit", await flow.Post("/Rules/Edit?handler=SaveSources", form, ("Id", ruleId), ("SourceEntryIdsInOrder", sourceEntryId), ("CnameTargets", "")));
        Assert.StartsWith("/Rules/Edit", await flow.Post("/Rules/Edit?handler=MoveSource", form, ("Id", ruleId), ("SourceId", sourceEntryId), ("Dir", "1")));
        Assert.StartsWith("/Rules/Edit", await flow.Post("/Rules/Edit?handler=RemoveSource", form, ("Id", ruleId), ("SourceId", sourceEntryId)));
        Assert.StartsWith("/Rules/Edit", await flow.Post("/Rules/Edit?handler=AddSource", form, ("Id", ruleId), ("SourceEntryId", sourceEntryId)));
        Assert.StartsWith("/Rules/Edit", await flow.Post("/Rules/Edit?handler=RunNow", form, ("Id", ruleId)));
        Assert.Equal("/Rules", await flow.Post("/Rules/Edit?handler=Delete", form, ("Id", ruleId)));
    }

    [Fact]
    public async Task Dynamic_rule_forms_redirect_to_pages_that_exist()
    {
        var flow = new Flow(await _app.AdminAsync());

        var push = await flow.Post("/Sources/Edit?handler=Create", "/Sources/Edit", ("Name", "Dynamic flow receiver"), ("Kind", "Push"), ("IntervalSeconds", "0"));
        var zone = await flow.Post("/Domains/Edit?handler=Create", "/Domains/Edit", ("Name", "Dynamic flow zone"), ("Kind", "Netcup"), ("NcCustomer", "1"), ("NcKey", "k"), ("NcPass", "p"));

        var created = await flow.Post("/Rules/Dynamic?handler=Create", "/Rules/Dynamic", ("DynamicSourceId", Flow.IdOf(push)), ("DomainGroupId", Flow.IdOf(zone)), ("DynamicZone", "example.com"), ("DynamicPrefix", "dyn"));
        var id = Flow.IdOf(created);
        var form = $"/Rules/Dynamic?id={id}";

        Assert.StartsWith("/Rules/Dynamic", await flow.Post("/Rules/Dynamic?handler=Save", form, ("Id", id), ("Enabled", "true"), ("DynamicZone", "example.com"), ("DynamicPrefix", "dynamic")));
        Assert.Equal("/Rules", await flow.Post("/Rules/Dynamic?handler=Delete", form, ("Id", id)));
    }

    [Fact]
    public async Task User_forms_redirect_to_the_user_list()
    {
        var flow = new Flow(await _app.AdminAsync());

        Assert.Equal("/Users", await flow.Post("/Users/Edit?handler=Create", "/Users/Edit", ("NewUsername", "flow-user"), ("NewPassword", "flow-password-1")));
        Assert.Equal("/Users", await flow.Post("/Users/Edit?handler=Delete", "/Users/Edit?name=flow-user", ("Name", "flow-user")));
    }

    [Fact]
    public async Task An_unknown_item_goes_back_to_its_list()
    {
        var admin = await _app.AdminAsync();

        foreach (var (page, list) in new[] { ("/Sources/Edit?id=nope", "/Sources"), ("/Domains/Edit?id=nope", "/Domains"), ("/Rules/Edit?id=nope", "/Rules"),
                                              ("/Rules/Dynamic?id=nope", "/Rules"), ("/Users/Edit?name=nope", "/Users") })
        {
            var response = await admin.GetAsync(page);
            Assert.True(response.StatusCode == HttpStatusCode.Redirect, $"{page} answered {(int)response.StatusCode}");
            Assert.Equal(list, response.Headers.Location!.ToString());
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(list)).StatusCode);
        }
    }

    [Fact]
    public async Task System_settings_are_saved_and_the_page_is_shown_again()
    {
        var flow = new Flow(await _app.AdminAsync());

        var location = await flow.Post("/System", "/System", ("MinLogLevel", "Info"), ("LogRetentionDays", "14"), ("TimeZone", ""));

        Assert.StartsWith("/System", location);
    }
}
