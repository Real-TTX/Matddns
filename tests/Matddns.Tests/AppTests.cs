using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Matddns.Tests;

/// <summary>Runs the real app against a temporary data directory, without the background updater.</summary>
public sealed class AppFactory : WebApplicationFactory<Program>
{
    public const string AdminPassword = "integration-test-password";

    private readonly string _dataDirectory = Directory.CreateTempSubdirectory("matddns-app-").FullName;
    private HttpClient? _admin;

    public CapturingLoggerProvider Logs { get; } = new();

    public AppFactory()
    {
        // Program.cs reads these while the host is built (on first use), so they have to be set beforehand.
        Environment.SetEnvironmentVariable("MATDDNS_DATA", _dataDirectory);
        Environment.SetEnvironmentVariable("MATDDNS_ADMIN_PASSWORD", AdminPassword);
    }

    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>One signed-in admin shared by all tests: logins are rate limited (20 requests / 5 minutes).</summary>
    public async Task<HttpClient> AdminAsync() => _admin ??= await SignedInAsync("admin", AdminPassword);

    public async Task<HttpClient> SignedInAsync(string username, string password)
    {
        var client = NewClient();
        var response = await Login(client, username, password);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    public static Task<HttpResponseMessage> Login(HttpClient client, string username, string password) =>
        PostForm(client, "/Login", "/Login", ("Username", username), ("Password", password));

    /// <summary>Posts a form the way a browser does: fetch the page first to get the antiforgery token and cookie.</summary>
    public static async Task<HttpResponseMessage> PostForm(HttpClient client, string url, string formPage, params (string Key, string Value)[] fields)
    {
        var html = await (await client.GetAsync(formPage)).Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var form = fields.Select(f => KeyValuePair.Create(f.Key, f.Value)).Append(KeyValuePair.Create("__RequestVerificationToken", token));
        return await client.PostAsync(url, new FormUrlEncodedContent(form));
    }

    public static async Task<bool> HasAccess(HttpClient client) =>
        (await client.GetAsync("/Sources")).StatusCode == HttpStatusCode.OK;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>()); // no polling of external services
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_dataDirectory, recursive: true); } catch (IOException) { }
    }
}

public sealed class CapturingLoggerProvider : ILoggerProvider, ILogger
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public ILogger CreateLogger(string categoryName) => this;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Dispose() { }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Messages.Enqueue(formatter(state, exception));
}

public class AppTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _app;

    public AppTests(AppFactory app) => _app = app;

    [Fact]
    public async Task Anonymous_requests_are_redirected_to_the_login_page_and_carry_hardening_headers()
    {
        var response = await _app.NewClient().GetAsync("/Sources");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Login", response.Headers.Location!.ToString());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task Login_with_a_wrong_password_is_rejected()
    {
        var client = _app.NewClient();

        var response = await AppFactory.Login(client, "admin", "not-the-password");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Invalid credentials", await response.Content.ReadAsStringAsync());
        Assert.False(await AppFactory.HasAccess(client));
    }

    [Fact]
    public async Task Login_with_the_right_password_grants_access_regardless_of_username_case()
    {
        var client = await _app.SignedInAsync("ADMIN", AppFactory.AdminPassword);

        Assert.True(await AppFactory.HasAccess(client));
    }

    [Fact]
    public async Task Changing_a_password_ends_that_users_existing_sessions()
    {
        var admin = await _app.AdminAsync();
        await AppFactory.PostForm(admin, "/Users/Edit?handler=Create", "/Users/Edit",
            ("NewUsername", "second@example.com"), ("NewPassword", "second-password-1"));
        var second = await _app.SignedInAsync("second@example.com", "second-password-1");
        Assert.True(await AppFactory.HasAccess(second));

        await AppFactory.PostForm(admin, "/Users/Edit?handler=Save", "/Users/Edit?name=second@example.com",
            ("Name", "second@example.com"), ("NewPassword", "second-password-2"));

        Assert.False(await AppFactory.HasAccess(second));
        Assert.True(await AppFactory.HasAccess(admin));
        Assert.True(await AppFactory.HasAccess(await _app.SignedInAsync("second@example.com", "second-password-2")));
    }

    [Fact]
    public async Task Deleting_a_user_ends_that_users_sessions()
    {
        var admin = await _app.AdminAsync();
        await AppFactory.PostForm(admin, "/Users/Edit?handler=Create", "/Users/Edit", ("NewUsername", "temp"), ("NewPassword", "temp-password-1"));
        var temp = await _app.SignedInAsync("temp", "temp-password-1");
        Assert.True(await AppFactory.HasAccess(temp));

        await AppFactory.PostForm(admin, "/Users/Edit?handler=Delete", "/Users/Edit?name=temp", ("Name", "temp"));

        Assert.False(await AppFactory.HasAccess(temp));
    }

    [Fact]
    public async Task Changing_your_own_password_keeps_you_signed_in()
    {
        var admin = await _app.AdminAsync();
        await AppFactory.PostForm(admin, "/Users/Edit?handler=Create", "/Users/Edit", ("NewUsername", "self"), ("NewPassword", "self-password-1"));
        var self = await _app.SignedInAsync("self", "self-password-1");

        await AppFactory.PostForm(self, "/Users/Edit?handler=Save", "/Users/Edit?name=self", ("Name", "self"), ("NewPassword", "self-password-2"));

        Assert.True(await AppFactory.HasAccess(self));
    }

    [Fact]
    public async Task Passwords_shorter_than_the_minimum_are_refused()
    {
        var admin = await _app.AdminAsync();

        await AppFactory.PostForm(admin, "/Users/Edit?handler=Create", "/Users/Edit", ("NewUsername", "weakling"), ("NewPassword", "short"));

        var system = await (await admin.GetAsync("/System")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("weakling", system);
    }

    [Fact]
    public async Task Tokens_in_query_strings_never_reach_the_application_log()
    {
        const string secret = "SECRET-TOKEN-VALUE";

        await _app.NewClient().GetAsync($"/nic/update?token={secret}&ipv4=203.0.113.5");

        Assert.DoesNotContain(_app.Logs.Messages, message => message.Contains(secret));
    }

    [Fact]
    public async Task Anonymous_api_endpoints_are_rate_limited()
    {
        var client = _app.NewClient();

        var first = await client.GetAsync("/api/health");
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 70; i++)
            statuses.Add((await client.GetAsync("/api/health")).StatusCode);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }
}
