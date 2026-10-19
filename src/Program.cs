using System.Threading.RateLimiting;
using Matddns.Api;
using Matddns.Platform;
using Matddns.Services;
using MatPlatform;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

// The UI and the log are English: system messages (a refused connection, a timeout) must not follow the language of the host.
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);

// Request URLs carry secrets in their query strings (?token=, DynDNS update URLs with {password}), so neither the
// inbound request log nor HttpClientFactory's outbound request log may record them at Information level.
builder.Logging.AddFilter("Microsoft.AspNetCore", Microsoft.Extensions.Logging.LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", Microsoft.Extensions.Logging.LogLevel.Warning);

var dataDir = Environment.GetEnvironmentVariable("MATDDNS_DATA") ?? "/data";
Directory.CreateDirectory(dataDir);
var keysDir = Path.Combine(dataDir, "keys");
Directory.CreateDirectory(keysDir);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysDir))
    .SetApplicationName("Matddns");

builder.Services.AddRazorPages();
builder.Services.AddHttpClient();

builder.Services.AddSingleton(new PathOptions(dataDir));
builder.Services.AddSingleton<ConfigService>();
builder.Services.AddSingleton<LogService>();
builder.Services.AddSingleton<HistoryService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<PublicIpClient>();
builder.Services.AddSingleton<DnsLookupClient>();
builder.Services.AddSingleton<FritzboxClient>();
builder.Services.AddSingleton<UnifiClient>();
builder.Services.AddSingleton<UnifiCloudClient>();
builder.Services.AddSingleton<NetcupClient>();
builder.Services.AddSingleton<CloudflareClient>();
builder.Services.AddSingleton<HetznerClient>();
builder.Services.AddSingleton<GoDaddyClient>();
builder.Services.AddSingleton<MatddnsClient>();
builder.Services.AddSingleton<DynDnsClient>();
builder.Services.AddSingleton<SourceResolver>();
builder.Services.AddSingleton<ReachabilityChecker>();
builder.Services.AddSingleton<StatusService>();
builder.Services.AddSingleton<TimeZoneService>();
builder.Services.AddSingleton<Matddns.Support.Toolkit>();
builder.Services.AddSingleton<PushReceiver>();
builder.Services.AddSingleton<UpdaterService>(); // also injected by the management API (POST /api/v1/run)
builder.Services.AddHostedService(sp => sp.GetRequiredService<UpdaterService>());
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<UserService>();

// The Mat platform contract (MatDesign docs/PLATFORM.md): /api, /api/health, /api/state, /api/configure, /api/actions/{id}.
// Its key is MAT_API_KEY; without it the engine key (MATDDNS_API_KEY, config Matddns:ApiKey) opens /api too, so a caller that has
// only the engine key (an install from an older Mattiverse catalog) needs no second one. The engine API itself only takes the engine key.
if (string.IsNullOrWhiteSpace(builder.Configuration[MatPlatformSettings.ApiKeyVariable]))
{
    var engineKey = Environment.GetEnvironmentVariable("MATDDNS_API_KEY");
    if (string.IsNullOrWhiteSpace(engineKey)) engineKey = builder.Configuration["Matddns:ApiKey"];
    if (!string.IsNullOrWhiteSpace(engineKey)) builder.Configuration[MatPlatformSettings.ApiKeyVariable] = engineKey.Trim();
}
// The anonymous /api and /api/health keep the limit of the other monitoring endpoints (60 requests per minute and address).
var platform = builder.AddMatPlatform<MatddnsPlatform>(MatddnsPlatform.Describe, o => o.AnonymousPermitPerMinute = 60);

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(opt =>
    {
        opt.Cookie.Name = "matddns.auth";
        opt.Cookie.HttpOnly = true;
        opt.Cookie.SameSite = SameSiteMode.Lax;
        opt.LoginPath = "/Login";
        opt.LogoutPath = "/Logout";
        opt.ExpireTimeSpan = TimeSpan.FromDays(30);
        opt.SlidingExpiration = true;
        // End a session as soon as its account is deleted or its password changed (the cookie itself stays valid for 30 days).
        opt.Events.OnValidatePrincipal = async ctx =>
        {
            var name = ctx.Principal?.Identity?.Name;
            var stamp = ctx.Principal?.FindFirst(AuthService.SecurityStampClaim)?.Value;
            var config = ctx.HttpContext.RequestServices.GetRequiredService<ConfigService>();
            var currentStamp = config.Read(c =>
                c.Users.FirstOrDefault(u => u.Username.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } user
                    ? AuthService.SecurityStamp(user)
                    : null);
            if (currentStamp == null || stamp != currentStamp)
            {
                ctx.RejectPrincipal();
                await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }
        };
    })
    // Sign-in through MatIdentity, only with MATIDENTITY_AUTHORITY (nothing changes without it); the local form stays at /Login?local=1.
    .AddMatIdentitySignIn<MatddnsIdentityUsers>(platform, o =>
    {
        o.CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        o.LoginPath = "/Login";
        o.AccessDeniedPath = "/Login?local=1&denied=1";
    });

builder.Services.AddAuthorization(opt =>
{
    opt.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.Configure<Microsoft.AspNetCore.Mvc.RazorPages.RazorPagesOptions>(opt =>
{
    opt.Conventions.AllowAnonymousToPage("/Login");
    // throttle the login page per client IP so credentials can't be brute-forced
    opt.Conventions.AddPageApplicationModelConvention("/Login",
        m => m.EndpointMetadata.Add(new EnableRateLimitingAttribute("login")));
});

// Rate limiting: protect the anonymous surface (token-guarded DynDNS/peer endpoints, monitoring API) from brute force / floods.
// Partitioned per client IP. Behind a reverse proxy, configure ForwardedHeaders so this sees the real IP.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(5) }));
    options.AddPolicy("api", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
    // The anonymous monitoring state (GET /api/state without a key or token) is answered by a middleware, not by an endpoint, so an endpoint
    // policy never sees it: it gets the same limit as the "api" policy here. Everything else passes this limiter untouched.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        MonitoringCompatibility.IsAnonymousState(ctx.Request)
            ? RateLimitPartition.GetFixedWindowLimiter(
                "state:" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) })
            : RateLimitPartition.GetNoLimiter(string.Empty));
});

var app = builder.Build();

// Behind a TLS-terminating reverse proxy, honor X-Forwarded-Proto/For so the request scheme becomes https
// (making the auth cookie Secure via SameAsRequest) and the client IP is the real one (for rate limiting).
// Opt-in only: without a trusted proxy a client could spoof these headers.
if (Environment.GetEnvironmentVariable("MATDDNS_FORWARDED_HEADERS")?.Trim().ToLowerInvariant() is "1" or "true" or "yes")
{
    var fho = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
    fho.KnownIPNetworks.Clear(); // trust the proxy in front of us (typical container/reverse-proxy setup)
    fho.KnownProxies.Clear();
    app.UseForwardedHeaders(fho);
}

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    ctx.Response.Headers.XFrameOptions = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});

app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        // The service worker, manifest and offline page must never be served from a stale HTTP cache; assets that carry
        // their build tag (?v=) can be cached for good.
        var file = ctx.File.Name;
        ctx.Context.Response.Headers.CacheControl =
            file is "sw.js" or "manifest.webmanifest" or "offline.html" ? "no-cache"
            : ctx.Context.Request.Query.ContainsKey("v") ? "public, max-age=31536000, immutable"
            : "public, max-age=3600";
    }
});
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
// An anonymous GET /api/state is a monitor: it gets the old monitoring state (see MonitoringCompatibility).
app.UseMonitoringState();
app.MapRazorPages();

// Liveness + public monitoring endpoints (no authentication, for monitoring software).
app.MapGet("/healthz", () => "ok").AllowAnonymous();

// The platform contract (/api, /api/state with a key or token, /api/configure, /api/actions/{id}) and its /api/health, which also
// carries the old monitoring fields (sources, rules, ipChanges): one answer for the monitors and the platform.
app.MapMatPlatform();
app.MapMonitoringHealth();

// Peer pull: another Matddns instance reads this instance's source entries (IPs only, no secrets),
// authenticated by any DynDNS-Server token on this instance.
app.MapGet("/api/source", (HttpContext ctx, ConfigService config, StatusService status) =>
{
    var token = ctx.Request.Query["token"].FirstOrDefault();
    var valid = !string.IsNullOrEmpty(token) &&
                config.Read(c => c.Sources.Any(s => s.Kind == Matddns.Models.SourceKind.Push && s.Push != null && PushReceiver.TokenEquals(s.Push.Token, token)));
    if (!valid)
        return Results.Json(new { status = "unauthorized", message = "Invalid or missing token." }, statusCode: 401);
    var s = status.Build(0);
    return Results.Json(new { time = s.Time, sources = s.SourceList });
}).AllowAnonymous().RequireRateLimiting("api");

// DynDNS receiver: external devices push their IP into a "Push" source (token-protected).
// JSON API form: /api/update?token=<token>&ipv4=<v4>&ipv6=<v6>  (send either or both; legacy ?ip= auto-detects family).
app.MapGet("/api/update", async (HttpContext ctx, PushReceiver push) =>
{
    var q = ctx.Request.Query;
    var token = q["token"].FirstOrDefault();
    var hostname = q["hostname"].FirstOrDefault();
    var ipv4 = q["ipv4"].FirstOrDefault();
    var ipv6 = q["ipv6"].FirstOrDefault();
    var ipAuto = q["ip"].FirstOrDefault();
    var caller = ctx.Connection.RemoteIpAddress?.ToString();
    var r = await push.UpdateAsync(token, hostname, ipv4, ipv6, ipAuto, caller, ctx.RequestAborted);
    return r.Status switch
    {
        "ok" => Results.Json(new { status = "ok", changed = r.Changed, ipv4 = r.Ipv4, ipv6 = r.Ipv6, time = DateTime.UtcNow }, statusCode: 200),
        "no-ip" => Results.Json(new { status = "no-ip", message = "Supply ipv4 and/or ipv6 (or ip)." }, statusCode: 400),
        "no-host" => Results.Json(new { status = "no-host", message = r.Message ?? "Hostname not allowed for this token." }, statusCode: 403),
        "error" => Results.Json(new { status = "error", message = r.Message ?? "Update failed." }, statusCode: 502),
        _ => Results.Json(new { status = "unauthorized", message = "Invalid or missing token." }, statusCode: 401),
    };
}).AllowAnonymous().RequireRateLimiting("api");

// dyndns2-compatible (routers / FRITZ!Box / UniFi): /nic/update and the shorter /update alias.
// Token via HTTP Basic auth password or ?token= (literal, or a client placeholder like %p); ipv4/ipv6 (or myip) set the address(es).
// A dynamic receiver also reads ?hostname= and writes that record. Keeps the dyndns2 plain-text protocol these clients expect.
async Task<IResult> DynDns2Update(HttpContext ctx, PushReceiver push)
{
    var q = ctx.Request.Query;
    var token = PushReceiver.BasicAuthPassword(ctx.Request.Headers.Authorization.FirstOrDefault())
                ?? q["token"].FirstOrDefault();
    var hostname = q["hostname"].FirstOrDefault();
    // ipv4/ipv6 are canonical; myip/myipv6 accepted silently for stock dyndns2 clients.
    var ipv4 = q["ipv4"].FirstOrDefault() ?? q["myip"].FirstOrDefault();
    var ipv6 = q["ipv6"].FirstOrDefault() ?? q["myipv6"].FirstOrDefault() ?? q["myip6"].FirstOrDefault();
    var ipAuto = q["ip"].FirstOrDefault();
    var caller = ctx.Connection.RemoteIpAddress?.ToString();
    var r = await push.UpdateAsync(token, hostname, ipv4, ipv6, ipAuto, caller, ctx.RequestAborted);
    var body = r.Status switch
    {
        "unauthorized" => "badauth",
        "no-ip" => "nohost",
        "no-host" => "nohost",
        "error" => "dnserr",
        _ => $"{(r.Changed ? "good" : "nochg")} {string.Join(" ", new[] { r.Ipv4, r.Ipv6 }.Where(x => x != null))}".Trim(),
    };
    return Results.Text(body, "text/plain", statusCode: 200); // dyndns2 carries status in the body
}
app.MapGet("/nic/update", DynDns2Update).AllowAnonymous().RequireRateLimiting("api");
app.MapGet("/update", DynDns2Update).AllowAnonymous().RequireRateLimiting("api");

// Management API for Mattiverse (/api/v1): guarded by the X-Api-Key header (MATDDNS_API_KEY), not by the cookie login.
app.MapManagementApi();

app.Services.GetRequiredService<ConfigService>().EnsureLoaded();
// An install from before the history of IP changes: take the updates of the log over once.
app.Services.GetRequiredService<HistoryService>().SeedFrom(
    app.Services.GetRequiredService<LogService>().EntriesOfLevel(Matddns.Services.LogLevel.Update, HistoryService.MaxEntries).Reverse());

app.Run();

public partial class Program; // makes the entry point visible to WebApplicationFactory in the integration tests

public record PathOptions(string DataDir)
{
    public string ConfigFile => Path.Combine(DataDir, "config.json");
    public string LogFile => Path.Combine(DataDir, "log.txt");
}
