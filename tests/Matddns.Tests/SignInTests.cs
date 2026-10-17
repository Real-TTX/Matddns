using System.Net;
using System.Text.Json;
using Matddns.Platform;
using MatPlatform;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Matddns.Tests;

/// <summary>Without <c>MATIDENTITY_AUTHORITY</c> nothing changes: the local form, as before.</summary>
public sealed class LocalSignInTests(MatddnsFactory site) : IClassFixture<MatddnsFactory>
{
    [Fact]
    public async Task The_sign_in_page_is_the_local_form_and_it_works()
    {
        var browser = site.Browser();
        var unsigned = await browser.GetAsync("/Sources");
        Assert.Equal(HttpStatusCode.Redirect, unsigned.StatusCode);
        Assert.StartsWith("/Login", SignIn.Path(unsigned));

        var page = await browser.GetAsync("/Login");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-form\"", html);
        Assert.DoesNotContain("MatIdentity", html);

        var signedIn = await SignIn.PostLocalFormAsync(browser, "/Login", html);
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Contains(SignIn.SetCookies(signedIn), c => c.StartsWith(MatddnsFactory.SessionCookie + "=", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Sources")).StatusCode);

        // the sign-out goes back to the app, not to MatIdentity
        var signedOut = await browser.GetAsync("/Logout");
        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        Assert.Equal("/", SignIn.Path(signedOut));
    }

    [Fact]
    public async Task A_wrong_password_is_refused()
    {
        var browser = site.Browser();
        var html = await (await browser.GetAsync("/Login")).Content.ReadAsStringAsync();
        var response = await SignIn.PostLocalFormAsync(browser, "/Login", html, "admin", "wrong");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(SignIn.SetCookies(response), c => c.StartsWith(MatddnsFactory.SessionCookie + "=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task There_is_no_matidentity_callback()
        => Assert.NotEqual(HttpStatusCode.OK, (await site.Browser().GetAsync("/signin-oidc")).StatusCode);
}

/// <summary>With <c>MATIDENTITY_AUTHORITY</c>: the sign-in page goes straight to MatIdentity, with the authorization code and PKCE.</summary>
public sealed class IdentityAutoSignInTests(IdentityFactory site) : IClassFixture<IdentityFactory>
{
    [Fact]
    public async Task The_sign_in_page_goes_straight_to_matidentity_with_code_and_pkce()
    {
        var response = await site.Browser().GetAsync("/Login");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var location = response.Headers.Location!;
        Assert.StartsWith(IdentityFactory.Authority + "/connect/authorize", location.OriginalString);
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("matddns", query["client_id"].ToString());
        Assert.Equal("code", query["response_type"].ToString());
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        Assert.False(string.IsNullOrEmpty(query["code_challenge"].ToString()));
        Assert.EndsWith("/signin-oidc", query["redirect_uri"].ToString());
    }

    [Fact]
    public async Task A_page_that_needs_a_sign_in_ends_at_matidentity()
    {
        var browser = site.Browser();
        var first = await browser.GetAsync("/Rules");
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        var login = SignIn.Path(first);
        Assert.StartsWith("/Login", login);

        var second = await browser.GetAsync(login);
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        Assert.StartsWith(IdentityFactory.Authority, second.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task The_definition_names_matidentity_and_a_bad_token_is_401()
    {
        using var doc = JsonDocument.Parse(await site.Anonymous().GetStringAsync("/api"));
        Assert.Equal(IdentityFactory.Authority, doc.RootElement.GetProperty("auth").GetProperty("identity").GetString());

        var bearer = site.Anonymous();
        bearer.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.GetAsync("/api/configure")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await site.Api().GetAsync("/api/configure")).StatusCode);
    }

    [Theory]
    [InlineData("/Login?local=1", false, false)]
    [InlineData("/Login?signedOut=1", false, false)]
    [InlineData("/Login?local=1&ssoError=1", true, false)]
    [InlineData("/Login?local=1&denied=1", false, true)]
    public async Task The_local_form_stays_reachable(string url, bool ssoError, bool denied)
    {
        var page = await site.Browser().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-form\"", html);
        Assert.Contains("id=\"login-identity\"", html);
        Assert.Equal(ssoError, html.Contains("id=\"login-sso-error\""));
        Assert.Equal(denied, html.Contains("id=\"login-denied\""));
        SignIn.IsMatUiPage(site, html);
    }

    [Fact]
    public async Task The_local_sign_in_works_with_matidentity_configured()
    {
        var browser = site.Browser();
        var html = await (await browser.GetAsync("/Login?local=1")).Content.ReadAsStringAsync();
        var signedIn = await SignIn.PostLocalFormAsync(browser, "/Login?local=1", html);
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Sources")).StatusCode);

        // a local session has no ID token: the sign-out stays here and lands on ?signedOut=1 (no automatic sign-in)
        var signedOut = await browser.GetAsync("/Logout");
        Assert.Equal("/Login?signedOut=1", SignIn.Path(signedOut));
    }
}

/// <summary>MatIdentity is configured but cannot be reached: the sign-in page shows the local form with a message.</summary>
public sealed class UnreachableIdentityTests(UnreachableIdentityFactory site) : IClassFixture<UnreachableIdentityFactory>
{
    [Fact]
    public async Task The_local_form_is_the_way_in()
    {
        var page = await site.Browser().GetAsync("/Login");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-form\"", html);
        Assert.Contains("id=\"login-sso-error\"", html);
    }
}

/// <summary>The person of MatIdentity: found by <c>sub</c>, created, refused when the match is not clear, and signed out there too.</summary>
public sealed class IdentityUsersTests(IdentityFactory site) : IClassFixture<IdentityFactory>
{
    [Fact]
    public async Task A_new_person_becomes_an_account_without_a_password_and_a_session_with_the_id_token()
    {
        var (target, cookie) = await SignInAsync(new MatIdentityPerson("sub-new", "new@example.com", "New Person", "newbie", "admin", "id-token-new", new()));
        Assert.Equal("/Rules", target);
        Assert.Contains(MatddnsFactory.SessionCookie + "=", cookie);

        var user = site.User("newbie")!;
        Assert.Equal("sub-new", user.ExternalSubject);
        Assert.Equal(string.Empty, user.PasswordHash);

        // the next sign-in finds the same account by the subject
        await SignInAsync(new MatIdentityPerson("sub-new", "changed@example.com", "New Person", "renamed", null, "id-token-2", new()));
        Assert.Equal(1, site.Read(c => c.Users.Count(u => u.ExternalSubject == "sub-new")));
        Assert.Null(site.User("renamed"));
    }

    [Fact]
    public async Task Without_a_user_name_the_name_comes_from_the_address_and_a_taken_one_gets_a_number()
    {
        await SignInAsync(new MatIdentityPerson("sub-plain", "plain@example.com", null, null, null, null, new()));
        Assert.Equal("sub-plain", site.User("plain")!.ExternalSubject);

        await SignInAsync(new MatIdentityPerson("sub-plain-2", "plain@other.example", null, null, null, null, new()));
        Assert.Equal("sub-plain-2", site.User("plain-2")!.ExternalSubject);
    }

    [Fact]
    public async Task Several_matching_accounts_are_refused()
    {
        site.AddUser("twin", "password-1");
        site.AddUser("twin@example.com", "password-1");

        var (target, cookie) = await SignInAsync(new MatIdentityPerson("sub-twin", "twin@example.com", null, "twin", null, null, new()));
        Assert.Equal("/Login?local=1&denied=1", target);
        Assert.DoesNotContain(MatddnsFactory.SessionCookie + "=", cookie);
        Assert.False(site.Read(c => c.Users.Any(u => u.ExternalSubject == "sub-twin")));
    }

    [Fact]
    public async Task A_match_linked_with_another_person_is_refused()
    {
        site.AddUser("linked-other", "password-1", externalSubject: "sub-someone-else");

        var (target, _) = await SignInAsync(new MatIdentityPerson("sub-intruder", null, null, "linked-other", null, null, new()));
        Assert.Equal("/Login?local=1&denied=1", target);
        Assert.Equal("sub-someone-else", site.User("linked-other")!.ExternalSubject);
    }

    [Fact]
    public async Task The_sign_out_ends_the_session_at_matidentity_with_the_id_token()
    {
        using var scope = site.Services.CreateScope();
        var http = site.Context(scope);
        await MatIdentitySignIn.CompleteSignInAsync(http, new MatIdentityPerson("sub-out", "out@example.com", "Out", "out", null, "id-token-out", new()), "/");
        var cookies = new CookieContainer();
        foreach (var header in http.Response.Headers.SetCookie)
            cookies.SetCookies(new Uri("http://localhost"), header!);
        var browser = site.Browser(cookies);

        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Sources")).StatusCode);

        var signedOut = await browser.GetAsync("/Logout");
        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        var location = signedOut.Headers.Location!.OriginalString;
        Assert.StartsWith(IdentityFactory.Authority + "/connect/logout", location);
        Assert.Equal("id-token-out", QueryHelpers.ParseQuery(new Uri(location).Query)["id_token_hint"].ToString());
    }

    /// <summary>What the callback /signin-oidc does after MatIdentity, without a running MatIdentity: where the browser goes and which cookies it gets.</summary>
    private async Task<(string Target, string SetCookie)> SignInAsync(MatIdentityPerson person)
    {
        using var scope = site.Services.CreateScope();
        var http = site.Context(scope);
        var target = await MatIdentitySignIn.CompleteSignInAsync(http, person, "/Rules");
        return (target, http.Response.Headers.SetCookie.ToString());
    }
}

/// <summary>
/// An account whose name matches a person of MatIdentity is linked only after its password: anyone could have created an account with the name of a
/// victim beforehand and received the victim in it at the first sign-in through MatIdentity. Each test starts its own app.
/// </summary>
public sealed class PendingLinkTests
{
    private const string ClaimedName = "claimer";
    private const string ClaimedPassword = "claimer-knows-this";

    [Fact]
    public async Task A_pre_claimed_name_is_not_entered_and_the_browser_lands_on_the_local_sign_in()
    {
        using var site = Site();
        var users = site.Read(c => c.Users.Count);

        var (target, setCookie, browser) = await MatIdentitySignInAsync(site, Person());

        Assert.Equal("/Login?local=1&link=1", target);
        Assert.DoesNotContain(MatddnsFactory.SessionCookie + "=", setCookie);
        Assert.Contains(MatIdentityPendingLink.CookieName + "=", setCookie);
        Assert.Null(site.User(ClaimedName)!.ExternalSubject);
        Assert.Equal(users, site.Read(c => c.Users.Count));

        var page = await browser.GetAsync(target);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-link\"", html);
        Assert.Contains("id=\"login-link-cancel\"", html);
        Assert.Contains("id=\"login-form\"", html);
        SignIn.IsMatUiPage(site, html);
    }

    [Fact]
    public async Task The_local_sign_in_of_the_matched_account_links_it_with_the_id_token()
    {
        using var site = Site();
        var (target, _, browser) = await MatIdentitySignInAsync(site, Person());
        var html = await (await browser.GetAsync(target)).Content.ReadAsStringAsync();

        // a wrong password links nothing and leaves the pending link in place
        var wrong = await SignIn.PostLocalFormAsync(browser, target, html, ClaimedName, "wrong");
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        html = await wrong.Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-link\"", html);
        Assert.Null(site.User(ClaimedName)!.ExternalSubject);

        var signedIn = await SignIn.PostLocalFormAsync(browser, target, html, ClaimedName, ClaimedPassword);
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Contains(SignIn.SetCookies(signedIn), c => c.StartsWith(MatIdentityPendingLink.CookieName + "=;", StringComparison.Ordinal));
        Assert.Equal("sub-victim", site.User(ClaimedName)!.ExternalSubject);
        Assert.Contains(site.Services.GetRequiredService<Services.LogService>().Recent(50), e => e.Message.Contains("linked with MatIdentity"));

        // the session carries the ID token: the sign-out ends it at MatIdentity
        var signedOut = await browser.GetAsync("/Logout");
        var location = signedOut.Headers.Location!.OriginalString;
        Assert.StartsWith(IdentityFactory.Authority + "/connect/logout", location);
        Assert.Equal("id-token-victim", QueryHelpers.ParseQuery(new Uri(location).Query)["id_token_hint"].ToString());

        // from now on MatIdentity finds the account by the subject and signs in directly
        var (next, cookie, _) = await MatIdentitySignInAsync(site, Person());
        Assert.Equal("/", next);
        Assert.Contains(MatddnsFactory.SessionCookie + "=", cookie);
    }

    [Fact]
    public async Task Another_account_signing_in_links_nothing_and_drops_the_pending_link()
    {
        using var site = Site();
        var (target, _, browser) = await MatIdentitySignInAsync(site, Person());
        var html = await (await browser.GetAsync(target)).Content.ReadAsStringAsync();

        var signedIn = await SignIn.PostLocalFormAsync(browser, target, html, "admin", "admin");
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Contains(SignIn.SetCookies(signedIn), c => c.StartsWith(MatIdentityPendingLink.CookieName + "=;", StringComparison.Ordinal));

        Assert.Null(site.User(ClaimedName)!.ExternalSubject);
        Assert.Null(site.User("admin")!.ExternalSubject);
        Assert.False(site.Read(c => c.Users.Any(u => u.ExternalSubject == "sub-victim")));

        // and afterwards not the matching account either: the link is gone
        var again = site.Browser();
        var form = await (await again.GetAsync("/Login?local=1")).Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(again, "/Login?local=1", form, ClaimedName, ClaimedPassword)).StatusCode);
        Assert.Null(site.User(ClaimedName)!.ExternalSubject);
    }

    [Fact]
    public async Task Cancel_deletes_the_pending_link_and_a_later_sign_in_links_nothing()
    {
        using var site = Site();
        var (target, _, browser) = await MatIdentitySignInAsync(site, Person());
        var html = await (await browser.GetAsync(target)).Content.ReadAsStringAsync();

        var cancelled = await browser.PostAsync("/Login?handler=CancelLink", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SignIn.AntiforgeryToken(html),
        }));
        Assert.Equal(HttpStatusCode.Redirect, cancelled.StatusCode);
        Assert.Equal("/Login?local=1", SignIn.Path(cancelled));
        Assert.Contains(SignIn.SetCookies(cancelled), c => c.StartsWith(MatIdentityPendingLink.CookieName + "=;", StringComparison.Ordinal));

        html = await (await browser.GetAsync(target)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("id=\"login-link\"", html);
        Assert.Contains("id=\"login-link-expired\"", html);

        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, target, html, ClaimedName, ClaimedPassword)).StatusCode);
        Assert.Null(site.User(ClaimedName)!.ExternalSubject);
    }

    [Fact]
    public async Task A_matching_address_needs_the_proof_as_well()
    {
        using var site = Site();
        site.AddUser("someone@example.com", "password-1");
        var (target, setCookie, _) = await MatIdentitySignInAsync(site, new MatIdentityPerson("sub-mail", "someone@example.com", "Someone", null, null, null, new()));

        Assert.Equal("/Login?local=1&link=1", target);
        Assert.DoesNotContain(MatddnsFactory.SessionCookie + "=", setCookie);
        Assert.Null(site.User("someone@example.com")!.ExternalSubject);
    }

    [Fact]
    public async Task A_forged_pending_cookie_is_ignored()
    {
        using var site = Site();
        var json = JsonSerializer.Serialize(new MatIdentityPendingLink("sub-forged", null, null, ClaimedName, null, null, ClaimedName), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await AssertIgnoredAsync(site, Uri.EscapeDataString(json));
    }

    [Fact]
    public async Task An_expired_pending_cookie_is_ignored()
    {
        using var site = Site();
        var json = JsonSerializer.Serialize(new MatIdentityPendingLink("sub-expired", null, null, ClaimedName, null, null, ClaimedName), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var value = site.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(MatIdentityPendingLink.Purpose).ToTimeLimitedDataProtector()
            .Protect(json, DateTimeOffset.UtcNow.AddSeconds(-1));
        await AssertIgnoredAsync(site, value);
    }

    /// <summary>The app with the account that carries the victim's name; whoever created it knows its password.</summary>
    private static IdentityFactory Site()
    {
        var site = new IdentityFactory();
        site.AddUser(ClaimedName, ClaimedPassword);
        return site;
    }

    private static MatIdentityPerson Person() => new("sub-victim", "victim@example.com", "Victim", ClaimedName, null, "id-token-victim", new());

    /// <summary>The sign-in through MatIdentity (what /signin-oidc does), then a browser with the cookies it got.</summary>
    private static async Task<(string Target, string SetCookie, HttpClient Browser)> MatIdentitySignInAsync(IdentityFactory site, MatIdentityPerson person)
    {
        using var scope = site.Services.CreateScope();
        var http = site.Context(scope);
        var target = await MatIdentitySignIn.CompleteSignInAsync(http, person, "/");
        var cookies = new CookieContainer();
        foreach (var header in http.Response.Headers.SetCookie)
            cookies.SetCookies(new Uri("http://localhost"), header!);
        return (target, http.Response.Headers.SetCookie.ToString(), site.Browser(cookies));
    }

    /// <summary>A pending cookie the app did not make (or made too long ago) links nothing.</summary>
    private static async Task AssertIgnoredAsync(IdentityFactory site, string value)
    {
        var cookies = new CookieContainer();
        cookies.Add(new Uri("http://localhost"), new Cookie(MatIdentityPendingLink.CookieName, value));
        var browser = site.Browser(cookies);

        var html = await (await browser.GetAsync("/Login?local=1&link=1")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("id=\"login-link\"", html);
        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Login?local=1&link=1", html, ClaimedName, ClaimedPassword)).StatusCode);
        Assert.Null(site.User(ClaimedName)!.ExternalSubject);
    }
}
