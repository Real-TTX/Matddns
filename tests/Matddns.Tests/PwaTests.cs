using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Matddns.Tests;

/// <summary>The installable-app pieces: manifest, service worker, offline page and the page head. (The browser behaviour itself is checked by hand / in a real browser.)</summary>
[Collection("app")]
public class PwaTests
{
    private readonly AppFactory _app;

    public PwaTests(AppFactory app) => _app = app;

    [Fact]
    public async Task Manifest_is_a_standalone_web_app_manifest_and_every_icon_exists()
    {
        var client = _app.NewClient();

        var response = await client.GetAsync("/manifest.webmanifest");

        Assert.Equal("application/manifest+json", response.Content.Headers.ContentType?.MediaType);
        using var manifest = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = manifest.RootElement;
        Assert.Equal("standalone", root.GetProperty("display").GetString());
        Assert.Equal("/", root.GetProperty("start_url").GetString());

        var icons = root.GetProperty("icons").EnumerateArray().ToList();
        Assert.Contains(icons, i => i.GetProperty("sizes").GetString() == "192x192");
        Assert.Contains(icons, i => i.GetProperty("sizes").GetString() == "512x512");
        Assert.Contains(icons, i => i.GetProperty("purpose").GetString() == "maskable");
        foreach (var icon in icons)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(icon.GetProperty("src").GetString())).StatusCode);
    }

    [Theory]
    [InlineData("/sw.js")]
    [InlineData("/manifest.webmanifest")]
    [InlineData("/offline.html")]
    public async Task Service_worker_manifest_and_offline_page_work_without_a_session_and_are_never_cached_stale(string path)
    {
        var response = await _app.NewClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Assets_that_carry_a_build_tag_are_cached_for_good()
    {
        var response = await _app.NewClient().GetAsync("/css/site.css?v=test");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("immutable", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Pages_declare_the_installable_app_and_do_not_block_zoom()
    {
        var admin = await _app.AdminAsync();

        var html = await (await admin.GetAsync("/System")).Content.ReadAsStringAsync();

        Assert.Contains("rel=\"manifest\"", html);
        Assert.Contains("rel=\"apple-touch-icon\"", html);
        Assert.Contains("name=\"apple-mobile-web-app-capable\"", html);
        Assert.Contains("name=\"theme-color\"", html);
        var viewport = Regex.Match(html, "<meta name=\"viewport\" content=\"([^\"]*)\"").Groups[1].Value;
        Assert.Contains("viewport-fit=cover", viewport);   // use the whole screen, safe areas are handled in CSS
        Assert.DoesNotContain("user-scalable", viewport);  // pinch zoom stays available
        Assert.DoesNotContain("maximum-scale", viewport);
    }

    [Fact]
    public async Task The_dashboard_refreshes_itself_and_reloads_after_a_reconnect()
    {
        var admin = await _app.AdminAsync();

        var html = await (await admin.GetAsync("/")).Content.ReadAsStringAsync();

        Assert.Contains("data-autorefresh=\"30000\"", html);
        Assert.Contains("data-reconnect=\"reload\"", html);
    }

    [Fact]
    public async Task Forms_keep_their_page_after_a_reconnect_instead_of_reloading()
    {
        var admin = await _app.AdminAsync();

        var html = await (await admin.GetAsync("/Sources/Edit")).Content.ReadAsStringAsync();

        Assert.Contains("data-reconnect=\"keep\"", html);
        Assert.DoesNotContain("data-autorefresh", html);
    }
}
