using System.Net;
using MatPlatform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Matddns.Tests;

/// <summary>
/// The back-channel logout of MatIdentity (MatPlatform 1.4.0, <c>POST /signout-backchannel</c>): a signed logout token ends the session it names
/// (<c>sid</c>), or every session of the account linked with <c>sub</c>; the cookie of an ended session is signed out at its next request.
/// Each test starts its own app (the ended sessions are kept in config.json).
/// </summary>
public sealed class BackchannelLogoutTests
{
    private const string Path = "/signout-backchannel";

    [Fact]
    public async Task A_valid_token_ends_the_session_it_names_and_the_next_request_is_signed_out()
    {
        using var site = new IdentityFactory();
        var first = await SignInAsync(site, Person("sub-bc", "sid-1"));
        var second = await SignInAsync(site, Person("sub-bc", "sid-2"));
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/Sources")).StatusCode);

        var response = await PostAsync(site, Token("sub-bc", "sid-1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var signedOut = await first.GetAsync("/Sources");
        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        Assert.StartsWith("/Login", SignIn.Path(signedOut));
        // the other session of the same person goes on
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/Sources")).StatusCode);
        Assert.Contains("sid-1", site.User("bc")!.EndedSessions!.Select(e => e.Sid));
        Assert.Contains(site.Services.GetRequiredService<Services.LogService>().Recent(50), e => e.Message.Contains("MatIdentity signed out 'bc'"));
    }

    [Fact]
    public async Task The_subject_alone_ends_every_session_of_the_linked_account_and_a_new_sign_in_works()
    {
        using var site = new IdentityFactory();
        site.AddUser("linked", "linked-password", externalSubject: "sub-all");
        var a = await SignInAsync(site, Person("sub-all", "sid-a"));
        var b = await SignInAsync(site, Person("sub-all", "sid-b"));
        // a session with the password (no sid) of the same account ends as well
        var local = site.Browser();
        var form = await (await local.GetAsync("/Login?local=1")).Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(local, "/Login?local=1", form, "linked", "linked-password")).StatusCode);
        var other = await SignInAsync(site, Person("sub-other", "sid-other"));

        var response = await PostAsync(site, Token("sub-all", sessionId: null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        foreach (var browser in new[] { a, b, local })
            Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/Sources")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/Sources")).StatusCode);
        Assert.NotNull(site.User("linked")!.SignedOutAt);

        // signing in again afterwards starts a session that is not ended
        var again = await SignInAsync(site, Person("sub-all", "sid-c"));
        Assert.Equal(HttpStatusCode.OK, (await again.GetAsync("/Sources")).StatusCode);
    }

    [Fact]
    public async Task A_session_of_a_linked_account_carries_the_sid_of_the_pending_link()
    {
        using var site = new IdentityFactory();
        site.AddUser("claimer", "claimer-password");
        var (target, browser) = await CompleteAsync(site, Person("sub-link", "sid-link") with { PreferredUserName = "claimer" });
        Assert.Equal("/Login?local=1&link=1", target);
        var html = await (await browser.GetAsync(target)).Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, target, html, "claimer", "claimer-password")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Sources")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(site, Token("sub-link", "sid-link"))).StatusCode);

        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/Sources")).StatusCode);
    }

    [Fact]
    public async Task An_unknown_subject_is_no_error_and_ends_nothing()
    {
        using var site = new IdentityFactory();
        var session = await SignInAsync(site, Person("sub-known", "sid-known"));

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(site, Token("sub-nobody", "sid-nobody"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(site, Token("sub-nobody", null))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/Sources")).StatusCode);
    }

    [Fact]
    public async Task A_token_signed_with_another_key_is_400_and_ends_nothing()
    {
        using var site = new IdentityFactory();
        var session = await SignInAsync(site, Person("sub-forged", "sid-forged"));
        var forger = new SigningCredentials(new RsaSecurityKey(System.Security.Cryptography.RSA.Create(2048)) { KeyId = "test-key" }, SecurityAlgorithms.RsaSha256);

        var response = await PostAsync(site, Token("sub-forged", "sid-forged", forger));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await session.GetAsync("/Sources")).StatusCode);
    }

    [Fact]
    public async Task Without_matidentity_there_is_no_endpoint()
    {
        using var site = new MatddnsFactory();
        var form = new Dictionary<string, string> { ["logout_token"] = Token("sub-bc", "sid-1") };

        // nothing is mapped: anonymous, it is a path like any other unknown one (the sign-in comes first); signed in, it is 404
        var anonymous = await PostAsync(site, form["logout_token"]);
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.StartsWith("/Login", SignIn.Path(anonymous));

        var browser = site.Browser();
        var html = await (await browser.GetAsync("/Login")).Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await SignIn.PostLocalFormAsync(browser, "/Login", html)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.PostAsync(Path, new FormUrlEncodedContent(form))).StatusCode);
    }

    private static MatIdentityPerson Person(string subject, string sessionId)
        => new(subject, null, null, subject["sub-".Length..], null, "id-token-" + sessionId, new()) { SessionId = sessionId };

    /// <summary>The sign-in through MatIdentity (what /signin-oidc does), then a browser with the session.</summary>
    private static async Task<HttpClient> SignInAsync(IdentityFactory site, MatIdentityPerson person)
    {
        var (target, browser) = await CompleteAsync(site, person);
        Assert.Equal("/", target);
        return browser;
    }

    private static async Task<(string Target, HttpClient Browser)> CompleteAsync(IdentityFactory site, MatIdentityPerson person)
    {
        using var scope = site.Services.CreateScope();
        var http = site.Context(scope);
        var target = await MatIdentitySignIn.CompleteSignInAsync(http, person, "/");
        var cookies = new CookieContainer();
        foreach (var header in http.Response.Headers.SetCookie)
            cookies.SetCookies(new Uri("http://localhost"), header!);
        return (target, site.Browser(cookies));
    }

    private static Task<HttpResponseMessage> PostAsync(MatddnsFactory site, string token)
        => site.Anonymous().PostAsync(Path, new FormUrlEncodedContent(new Dictionary<string, string> { ["logout_token"] = token }));

    /// <summary>A logout token as MatIdentity writes it (<c>typ</c> <c>logout+jwt</c>, signed with the key of the fixed configuration).</summary>
    private static string Token(string? subject, string? sessionId, SigningCredentials? credentials = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object>
        {
            ["iss"] = IdentityFactory.Authority,
            ["aud"] = "matddns",
            ["iat"] = now,
            ["nbf"] = now,
            ["exp"] = now + 120,
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["events"] = new Dictionary<string, object> { [MatIdentityLogoutToken.EventType] = new Dictionary<string, object>() },
        };
        if (subject is not null) claims["sub"] = subject;
        if (sessionId is not null) claims["sid"] = sessionId;
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Claims = claims,
            TokenType = MatIdentityLogoutToken.TokenType,
            SigningCredentials = credentials ?? MatddnsFactory.Credentials,
        });
    }
}
