using System.Net;
using System.Text.RegularExpressions;
using Matddns.Models;
using Matddns.Services;
using MatPlatform;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Matddns.Tests;

/// <summary>
/// The whole app in memory, on its own empty data directory, without the background updater. <see cref="PlatformKey"/> is <c>MAT_API_KEY</c> and
/// <see cref="EngineKey"/> is <c>MATDDNS_API_KEY</c> (each only when asked for); with an <c>authority</c> the sign-in through MatIdentity is on, with a
/// fixed OpenID configuration so that no MatIdentity has to run (the tests only look at the redirects). Program reads its settings from environment
/// variables, so the test classes run one after the other (DisableTestParallelization). The administrator is <c>admin</c> / <c>admin</c>.
/// </summary>
public class MatddnsFactory : WebApplicationFactory<Program>
{
    public const string PlatformKey = "platform-key-0123456789";
    public const string EngineKey = "engine-key-9876543210";
    public const string SessionCookie = "matddns.auth";

    private readonly string _data = Path.Combine(Path.GetTempPath(), "matddns-tests-" + Guid.NewGuid().ToString("N"));
    private readonly bool _platformKey;
    private readonly bool _engineKey;
    private readonly string? _authority;
    private readonly bool _reachable;

    public MatddnsFactory()
        : this(platformKey: true, engineKey: true, authority: null)
    {
    }

    protected MatddnsFactory(bool platformKey, bool engineKey, string? authority, bool reachable = true)
    {
        _platformKey = platformKey;
        _engineKey = engineKey;
        _authority = authority;
        _reachable = reachable;
        Directory.CreateDirectory(_data);
        SetEnvironment();
    }

    public string DataDirectory => _data;

    private void SetEnvironment()
    {
        Environment.SetEnvironmentVariable("MATDDNS_DATA", _data);
        Environment.SetEnvironmentVariable("MATDDNS_ADMIN_PASSWORD", null);
        Environment.SetEnvironmentVariable("MATDDNS_API_KEY", _engineKey ? EngineKey : null);
        Environment.SetEnvironmentVariable(MatPlatformSettings.ApiKeyVariable, _platformKey ? PlatformKey : null);
        Environment.SetEnvironmentVariable(MatPlatformSettings.AuthorityVariable, _authority);
        Environment.SetEnvironmentVariable(MatPlatformSettings.ClientIdVariable, null);
        Environment.SetEnvironmentVariable(MatPlatformSettings.ClientSecretVariable, _authority is null ? null : "test-secret");
        Environment.SetEnvironmentVariable(MatPlatformSettings.InternalUrlVariable, null);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        // Program reads these before the host is built; set them again in case another factory changed them meanwhile.
        SetEnvironment();

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();

            if (_authority is not null && _reachable)
            {
                var configuration = new OpenIdConnectConfiguration
                {
                    Issuer = _authority,
                    AuthorizationEndpoint = _authority + "/connect/authorize",
                    TokenEndpoint = _authority + "/connect/token",
                    EndSessionEndpoint = _authority + "/connect/logout",
                };

                services.PostConfigure<OpenIdConnectOptions>(MatIdentitySignIn.Scheme, options =>
                {
                    options.Configuration = configuration;
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                });
                services.PostConfigure<JwtBearerOptions>(MatApiAccess.BearerScheme, options =>
                {
                    options.Configuration = configuration;
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                });
            }
        });
    }

    /// <summary>A browser that keeps cookies and does not follow redirects (the tests look at each step).</summary>
    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    /// <summary>A browser that already has <paramref name="cookies"/> (a session a test started).</summary>
    public HttpClient Browser(CookieContainer cookies)
    {
        _ = Server;
        return new HttpClient(new CookieContainerHandler(cookies) { InnerHandler = Server.CreateHandler() }) { BaseAddress = new Uri("http://localhost") };
    }

    /// <summary>A caller without credentials (a monitor).</summary>
    public HttpClient Anonymous() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>A caller with <paramref name="key"/> in <c>X-Api-Key</c> (like Mattiverse).</summary>
    public HttpClient Api(string key = PlatformKey)
    {
        var client = Anonymous();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    /// <summary>Changes the configuration of the app (only in the tests; the web layer asks the services).</summary>
    public void Configure(Action<AppConfig> change) => Services.GetRequiredService<ConfigService>().Mutate(change);

    public T Read<T>(Func<AppConfig, T> read) => Services.GetRequiredService<ConfigService>().Read(read);

    /// <summary>A HttpContext with the services of the app, for <see cref="MatIdentitySignIn.CompleteSignInAsync"/> without a running MatIdentity.</summary>
    public DefaultHttpContext Context(IServiceScope scope)
    {
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Scheme = "http";
        http.Request.Host = new HostString("localhost");
        return http;
    }

    /// <summary>Adds a local account with a password (like the user page).</summary>
    public void AddUser(string username, string password, string? externalSubject = null)
    {
        var hash = Services.GetRequiredService<AuthService>().Hash(password);
        Configure(c => c.Users.Add(new UserAccount { Username = username, PasswordHash = hash, ExternalSubject = externalSubject }));
    }

    public UserAccount? User(string username)
        => Read(c => c.Users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase)));

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        foreach (var variable in new[] { "MATDDNS_DATA", "MATDDNS_API_KEY", MatPlatformSettings.ApiKeyVariable, MatPlatformSettings.AuthorityVariable, MatPlatformSettings.ClientSecretVariable })
            Environment.SetEnvironmentVariable(variable, null);
        try { Directory.Delete(_data, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Only the engine key (an install from an older Mattiverse catalog): it opens the platform API too.</summary>
public sealed class EngineKeyOnlyFactory() : MatddnsFactory(platformKey: false, engineKey: true, authority: null);

/// <summary>No key at all: the protected part of /api answers 401, the engine API 503.</summary>
public sealed class NoKeyFactory() : MatddnsFactory(platformKey: false, engineKey: false, authority: null);

/// <summary>With <c>MATIDENTITY_AUTHORITY</c> (a MatIdentity that does not have to run).</summary>
public class IdentityFactory() : MatddnsFactory(platformKey: true, engineKey: true, authority: Authority)
{
    public const string Authority = "http://identity.test";
}

/// <summary>With <c>MATIDENTITY_AUTHORITY</c> on an address where nobody answers (MatIdentity is down).</summary>
public sealed class UnreachableIdentityFactory() : MatddnsFactory(platformKey: true, engineKey: true, authority: "http://127.0.0.1:9", reachable: false);

/// <summary>Helpers of the sign-in tests.</summary>
internal static partial class SignIn
{
    /// <summary>Posts the local form of <paramref name="page"/> (which <paramref name="browser"/> has just loaded).</summary>
    public static Task<HttpResponseMessage> PostLocalFormAsync(HttpClient browser, string url, string page, string user = "admin", string password = "admin")
        => browser.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = AntiforgeryToken(page),
            ["Username"] = user,
            ["Password"] = password,
        }));

    public static string AntiforgeryToken(string html) => TokenRegex().Match(html).Groups[1].Value;

    public static string Path(HttpResponseMessage response) => response.Headers.Location!.OriginalString.Replace("http://localhost", string.Empty);

    public static IEnumerable<string> SetCookies(HttpResponseMessage response)
        => response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [];

    [GeneratedRegex(@"name=""__RequestVerificationToken"" type=""hidden"" value=""([^""]+)""")]
    private static partial Regex TokenRegex();
}
