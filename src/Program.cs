using System.Threading.RateLimiting;
using Matddns.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddSingleton<PushReceiver>();
builder.Services.AddHostedService<UpdaterService>();

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

// Rate limiting: protect the anonymous, credential/token-guarded surface from brute force / floods.
// Partitioned per client IP. Behind a reverse proxy, configure ForwardedHeaders so this sees the real IP.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(5) }));
    options.AddPolicy("dyndns", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

// Behind a TLS-terminating reverse proxy, honor X-Forwarded-Proto/For so the request scheme becomes https
// (making the auth cookie Secure via SameAsRequest) and the client IP is the real one (for rate limiting).
// Opt-in only: without a trusted proxy a client could spoof these headers.
if (Environment.GetEnvironmentVariable("MATDDNS_FORWARDED_HEADERS")?.Trim().ToLowerInvariant() is "1" or "true" or "yes")
{
    var fho = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
    fho.KnownNetworks.Clear(); // trust the proxy in front of us (typical container/reverse-proxy setup)
    fho.KnownProxies.Clear();
    app.UseForwardedHeaders(fho);
}

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapRazorPages();

// Liveness + public monitoring endpoints (no authentication, for monitoring software).
app.MapGet("/healthz", () => "ok").AllowAnonymous();

app.MapGet("/api/health", (StatusService status) =>
{
    var s = status.Build(5);
    var body = new
    {
        status = s.Ok ? "ok" : "degraded",
        sources = s.Sources,
        rules = s.Rules,
        ipChanges = new { s.IpChanges.Total, s.IpChanges.Last24h, s.IpChanges.Last7d, s.IpChanges.LastChange },
        time = s.Time
    };
    return s.Ok ? Results.Json(body) : Results.Json(body, statusCode: 503);
}).AllowAnonymous();

app.MapGet("/api/state", (StatusService status) =>
{
    var s = status.Build(50);
    return Results.Json(new
    {
        status = s.Ok ? "ok" : "degraded",
        time = s.Time,
        sources = s.SourceList,
        rules = s.RuleList,
        ipChanges = s.IpChanges
    });
}).AllowAnonymous();

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
}).AllowAnonymous().RequireRateLimiting("dyndns");

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
}).AllowAnonymous().RequireRateLimiting("dyndns");

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
app.MapGet("/nic/update", DynDns2Update).AllowAnonymous().RequireRateLimiting("dyndns");
app.MapGet("/update", DynDns2Update).AllowAnonymous().RequireRateLimiting("dyndns");

app.Services.GetRequiredService<ConfigService>().EnsureLoaded();

app.Run();

public record PathOptions(string DataDir)
{
    public string ConfigFile => Path.Combine(DataDir, "config.json");
    public string LogFile => Path.Combine(DataDir, "log.txt");
}
