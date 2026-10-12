using System.Net;
using System.Text.RegularExpressions;
using Matddns.Models;
using Matddns.Services;
using Matddns.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Matddns.Tests;

/// <summary>The pages that use the MatUi toolkit: they render, only use what the toolkit has and keep to its rules about markup.</summary>
[Collection("app")]
public partial class UiTests
{
    private static readonly object SeedGate = new();
    private static Seeded? _seeded;

    private readonly AppFactory _app;

    public UiTests(AppFactory app) => _app = app;

    private sealed record Seeded(string[] Pages);

    /// <summary>One of every kind of source, domain group and rule, so the edit pages are rendered with real data (once for the whole run).</summary>
    private Seeded Seed()
    {
        lock (SeedGate)
        {
            if (_seeded != null) return _seeded;

            var pages = new List<string>();
            _app.Services.GetRequiredService<ConfigService>().Mutate(config =>
            {
                SourceGroup Source(SourceKind kind, Action<SourceGroup>? configure = null)
                {
                    var group = new SourceGroup { Name = $"Source {kind}", Kind = kind };
                    group.Entries.Add(new SourceEntry { Label = "Entry", CurrentIp = "203.0.113.7", CurrentIpv6 = "2001:db8::7", LastChecked = DateTime.UtcNow });
                    group.Entries.Add(new SourceEntry { Label = "Broken", LastError = "no route" });
                    configure?.Invoke(group);
                    config.Sources.Add(group);
                    pages.Add($"/Sources/Edit?id={group.Id}");
                    return group;
                }

                Source(SourceKind.PublicIp);
                Source(SourceKind.Static, g => g.Static = new StaticSettings { Ip = "203.0.113.9", Ipv6 = "2001:db8::9" });
                Source(SourceKind.Unifi, g => g.Unifi = new UnifiSettings { BaseUrl = "https://10.0.0.1", Username = "u", Password = "p" });
                Source(SourceKind.Dns, g => g.Dns = new DnsSettings { Hostname = "box.example.net" });
                Source(SourceKind.Fritzbox, g => g.Fritzbox = new FritzboxSettings { BaseUrl = "http://fritz.box:49000", Username = "u", Password = "p" });
                Source(SourceKind.Matddns, g => g.Matddns = new MatddnsLinkSettings { BaseUrl = "https://peer.example.com", Token = "t" });
                var push = Source(SourceKind.Push, g => g.Push = new PushSettings { Token = "push-token-value" });
                Source(SourceKind.UnifiCloud, g => g.UnifiCloud = new UnifiCloudSettings { ApiKey = "k" });

                DomainGroup Domain(DomainKind kind, Action<DomainGroup>? configure = null)
                {
                    var group = new DomainGroup { Name = $"Domain {kind}", Kind = kind };
                    group.Entries.Add(new DomainEntry { Hostname = "home.example.com", Type = DnsRecordType.A, RecordName = "home", Domain = "example.com" });
                    group.Entries.Add(new DomainEntry { Hostname = "alias.example.com", Type = DnsRecordType.CNAME, RecordName = "alias", Domain = "example.com" });
                    configure?.Invoke(group);
                    config.Domains.Add(group);
                    pages.Add($"/Domains/Edit?id={group.Id}");
                    return group;
                }

                Domain(DomainKind.DynDns, g => g.DynDns = new DynDnsSettings { UpdateUrl = "https://example.org/update?h={hostname}", Username = "u", Password = "p" });
                var netcup = Domain(DomainKind.Netcup, g => g.Netcup = new NetcupSettings { CustomerNumber = "1", ApiKey = "k", ApiPassword = "p", AllowDynamic = true });
                Domain(DomainKind.Cloudflare, g => g.Cloudflare = new CloudflareSettings { ApiToken = "t" });
                Domain(DomainKind.Hetzner, g => g.Hetzner = new HetznerSettings { ApiToken = "t" });
                Domain(DomainKind.GoDaddy, g => g.GoDaddy = new GoDaddySettings { ApiKey = "k", ApiSecret = "s" });
                Domain(DomainKind.Matddns, g => g.Matddns = new MatddnsLinkSettings { BaseUrl = "https://peer.example.com", Token = "t" });

                foreach (var entry in netcup.Entries)
                {
                    var rule = new Rule
                    {
                        DomainGroupId = netcup.Id,
                        DomainEntryId = entry.Id,
                        SourceEntryIdsInOrder = config.Sources[0].Entries.Select(e => e.Id).ToList(),
                        LastResult = "ok",
                        LastRun = DateTime.UtcNow,
                        LastValue = "203.0.113.7",
                    };
                    rule.CnameTargets = rule.SourceEntryIdsInOrder.Select(_ => "target.example.com").ToList();
                    config.Rules.Add(rule);
                    pages.Add($"/Rules/Edit?id={rule.Id}");
                }

                var dynamic = new Rule { Dynamic = true, DynamicSourceId = push.Id, DomainGroupId = netcup.Id, DynamicZone = "example.com", DynamicPrefix = "dynamic" };
                config.Rules.Add(dynamic);
                pages.Add($"/Rules/Dynamic?id={dynamic.Id}");
            });

            return _seeded = new Seeded(pages.ToArray());
        }
    }

    private static readonly string[] BasePages =
        ["/", "/Sources", "/Sources/Edit", "/Domains", "/Domains/Edit", "/Rules", "/Rules/Edit", "/Rules/Dynamic", "/Users", "/Users/Edit", "/System", "/Log"];

    private async Task<List<(string Path, string Html)>> RenderAllAsync()
    {
        var pages = BasePages.Concat(Seed().Pages).ToList();
        var admin = await _app.AdminAsync();
        var rendered = new List<(string, string)>();
        foreach (var path in pages)
        {
            var response = await admin.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path} answered {(int)response.StatusCode}");
            rendered.Add((path, await response.Content.ReadAsStringAsync()));
        }
        return rendered;
    }

    [Fact]
    public async Task Every_page_renders_with_data_and_names_its_page_in_the_title()
    {
        foreach (var (path, html) in await RenderAllAsync())
        {
            Assert.True(TitleRegex().IsMatch(html), $"{path} has no title");
            Assert.True(html.Contains("class=\"mat-app\""), $"{path} is not in the shell");
        }
    }

    [Fact]
    public async Task Pages_only_use_icons_the_toolkit_has()
    {
        var names = _app.Services.GetRequiredService<Toolkit>().IconNames;

        foreach (var (path, html) in await RenderAllAsync())
        {
            var used = IconUseRegex().Matches(html).Select(m => m.Groups[1].Value).Distinct();
            Assert.True(!used.Except(names).Any(), $"{path} uses unknown icons: {string.Join(", ", used.Except(names))}");
        }
    }

    [Fact]
    public async Task Pages_keep_their_markup_free_of_inline_style_and_script()
    {
        foreach (var (path, html) in await RenderAllAsync())
        {
            Assert.False(InlineStyleRegex().IsMatch(html), $"{path} has an inline style");
            Assert.False(InlineScriptRegex().IsMatch(html), $"{path} has an inline script");
            Assert.False(InlineHandlerRegex().IsMatch(html), $"{path} has an inline event handler");
        }
    }

    [Fact]
    public async Task Every_table_cell_of_a_page_says_what_it_is_for_the_phone_card()
    {
        foreach (var (path, html) in await RenderAllAsync())
        {
            foreach (Match cell in CellRegex().Matches(html))
            {
                var tag = cell.Value;
                Assert.True(tag.Contains("data-label=") || tag.Contains("mat-table__cell-actions") || tag.Contains("colspan="), $"{path}: a cell without data-label: {tag}");
            }
        }
    }

    [Fact]
    public async Task The_icon_sprite_is_inlined_once_per_page()
    {
        var html = await (await (await _app.AdminAsync()).GetAsync("/System")).Content.ReadAsStringAsync();

        Assert.Single(Regex.Matches(html, "class=\"mat-sprite\""));
    }

    [Fact]
    public void The_menu_and_the_logo_only_use_icons_the_toolkit_has()
    {
        var names = _app.Services.GetRequiredService<Toolkit>().IconNames;

        var menuIcons = Navigation.For(signedIn: true, sourceProblems: 1, ruleProblems: 1).SelectMany(g => g.Items).Select(i => i.Icon);

        Assert.Empty(menuIcons.Append(Ui.App.Glyph).Except(names));
    }

    [Fact]
    public async Task The_registered_accent_is_the_one_the_toolkit_gives_the_app()
    {
        var css = await (await _app.NewClient().GetAsync("/matui/matui.css")).Content.ReadAsStringAsync();

        var accent = Regex.Match(css, $"\\[data-app=\"{Ui.App.Id}\"\\]\\s*{{\\s*--mat-app-accent:\\s*(#[0-9a-fA-F]{{6}})").Groups[1].Value;

        Assert.Equal(Ui.App.Accent, accent, ignoreCase: true);
    }

    [Fact]
    public async Task The_bar_of_the_browser_and_the_installed_app_use_the_accent()
    {
        var client = _app.NewClient();

        var html = await (await client.GetAsync("/Login")).Content.ReadAsStringAsync();
        var manifest = await (await client.GetAsync("/manifest.webmanifest")).Content.ReadAsStringAsync();

        Assert.Contains($"<meta name=\"theme-color\" content=\"{Ui.App.Accent}\"", html);
        Assert.Contains(Ui.App.Accent, manifest, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/Sources", "/Sources")]
    [InlineData("/Sources/Edit", "/Sources")]
    [InlineData("/Users/Edit", "/Users")]
    [InlineData("/Rules/Dynamic", "/Rules")]
    [InlineData("/Log", "/Log")]
    [InlineData("/Sourcesx", null)]
    [InlineData("/Nothing", null)]
    public void The_menu_item_of_a_page_is_the_longest_address_the_path_continues(string path, string? expected)
    {
        var menu = Navigation.For(signedIn: true, sourceProblems: 0, ruleProblems: 0);

        Assert.Equal(expected, Navigation.ActiveHref(menu, path));
    }

    [Fact]
    public void A_visitor_without_a_sign_in_only_sees_the_dashboard()
    {
        var items = Navigation.For(signedIn: false, sourceProblems: 0, ruleProblems: 0).SelectMany(g => g.Items).ToList();

        Assert.Equal(["/"], items.Select(i => i.Href));
    }

    [Fact]
    public void Problems_count_the_failing_entries_and_the_rules_with_a_problem()
    {
        var config = new AppConfig();
        var group = new SourceGroup();
        group.Entries.Add(new SourceEntry { LastError = "x" });
        group.Entries.Add(new SourceEntry());
        config.Sources.Add(group);
        config.Rules.Add(new Rule { LastResult = "no source" });
        config.Rules.Add(new Rule { LastResult = "ok" });
        config.Rules.Add(new Rule { LastResult = "err: boom", Enabled = false });

        Assert.Equal((1, 1), Navigation.Problems(config));
    }

    [Fact]
    public async Task Created_users_are_listed_and_you_cannot_delete_yourself()
    {
        var admin = await _app.AdminAsync();
        await AppFactory.PostForm(admin, "/Users/Edit?handler=Create", "/Users/Edit", ("NewUsername", "listed-user"), ("NewPassword", "listed-password-1"));

        var list = await (await admin.GetAsync("/Users")).Content.ReadAsStringAsync();
        var own = await (await admin.GetAsync("/Users/Edit?name=admin")).Content.ReadAsStringAsync();
        var other = await (await admin.GetAsync("/Users/Edit?name=listed-user")).Content.ReadAsStringAsync();

        Assert.Contains("listed-user", list);
        Assert.DoesNotContain("handler=Delete", own);
        Assert.Contains("handler=Delete", other);
    }

    [GeneratedRegex("<title>[^<]+</title>")]
    private static partial Regex TitleRegex();

    [GeneratedRegex("#mat-i-([a-z0-9-]+)")]
    private static partial Regex IconUseRegex();

    [GeneratedRegex(@"<[a-z][^>]*\sstyle\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex InlineStyleRegex();

    [GeneratedRegex(@"<script(?![^>]*\ssrc=)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InlineScriptRegex();

    [GeneratedRegex(@"<[a-z][^>]*\son[a-z]+\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex InlineHandlerRegex();

    [GeneratedRegex(@"<td\b[^>]*>(?!</td>)", RegexOptions.IgnoreCase)]   // an empty cell (a spacer in the row of a child) has nothing to label
    private static partial Regex CellRegex();
}
