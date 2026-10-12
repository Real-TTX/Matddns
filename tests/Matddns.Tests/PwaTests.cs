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

    [Theory]
    [InlineData("/matui/matui.css?v=test")]
    [InlineData("/matui/matui.js?v=test")]
    [InlineData("/matui/matui.boot.js?v=test")]
    [InlineData("/css/matddns.css?v=test")]
    [InlineData("/js/matddns.js?v=test")]
    public async Task Assets_that_carry_a_build_tag_are_cached_for_good(string path)
    {
        var response = await _app.NewClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("immutable", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Assets_without_a_build_tag_are_revalidated()
    {
        var response = await _app.NewClient().GetAsync("/matui/matui.css");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("immutable", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task The_service_worker_only_precaches_files_that_exist()
    {
        var client = _app.NewClient();
        var worker = await (await client.GetAsync("/sw.js")).Content.ReadAsStringAsync();
        var offlineUrl = Regex.Match(worker, @"const OFFLINE_URL = '([^']+)'").Groups[1].Value;
        var precache = Regex.Match(worker, @"const PRECACHE = \[(.*?)\];", RegexOptions.Singleline).Groups[1].Value
            .Replace("OFFLINE_URL", $"'{offlineUrl}'");

        var paths = Regex.Matches(precache, @"'(/[^']+)'").Select(m => m.Groups[1].Value).ToList();

        Assert.Contains("/offline.html", paths);
        Assert.Contains("/matui/matui.css?v=", paths);
        foreach (var path in paths)
            Assert.True((await client.GetAsync(path.EndsWith("?v=") ? path + "test" : path)).StatusCode == HttpStatusCode.OK, $"{path} is not served");
    }

    [Fact]
    public async Task The_offline_page_stands_on_its_own_and_only_needs_what_the_service_worker_keeps()
    {
        var client = _app.NewClient();
        var page = await (await client.GetAsync("/offline.html")).Content.ReadAsStringAsync();
        var worker = await (await client.GetAsync("/sw.js")).Content.ReadAsStringAsync();

        var needed = Regex.Matches(page, @"(?:src|href)=""(/[^""#]+)""").Select(m => m.Groups[1].Value).Where(p => !p.StartsWith("/offline")).ToList();

        Assert.DoesNotMatch(@"<script(?![^>]*\ssrc=)", page);   // no inline script or style: the app's content security rules apply to this page too
        Assert.DoesNotMatch(@"<style|\sstyle=", page);
        Assert.Contains("matui.css", string.Concat(needed));
        foreach (var path in needed)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
            Assert.Contains(path, worker);   // listed in the precache, so it is there when the network is not
        }
    }

    [Fact]
    public async Task Pages_declare_the_installable_app_and_do_not_block_zoom()
    {
        var admin = await _app.AdminAsync();

        var html = await (await admin.GetAsync("/System")).Content.ReadAsStringAsync();

        Assert.Contains("rel=\"manifest\"", html);
        Assert.Contains("rel=\"apple-touch-icon\"", html);
        Assert.Contains("name=\"apple-mobile-web-app-capable\"", html);
        Assert.Contains("name=\"apple-mobile-web-app-status-bar-style\" content=\"default\"", html);   // the bar keeps the colour of the page (the toolkit's light/dark themes)
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
