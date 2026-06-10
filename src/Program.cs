using Matddns.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

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
builder.Services.AddSingleton<UnifiClient>();
builder.Services.AddSingleton<NetcupClient>();
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
});

var app = builder.Build();

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
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

// DynDNS receiver: external devices push their IP into a "Push" source (token-protected).
// Simple form: /api/update?token=<token>&ip=<ip>   (ip optional -> caller IP)
app.MapGet("/api/update", (HttpContext ctx, PushReceiver push) =>
{
    var token = ctx.Request.Query["token"].FirstOrDefault();
    var ip = ctx.Request.Query["ip"].FirstOrDefault() ?? ctx.Request.Query["myip"].FirstOrDefault();
    var caller = ctx.Connection.RemoteIpAddress?.ToString();
    var (statusCode, body) = push.Update(token, ip, caller);
    return Results.Text(body, "text/plain", statusCode: statusCode);
}).AllowAnonymous();

// dyndns2-compatible (routers / FRITZ!Box): /nic/update?hostname=&myip=  with HTTP Basic auth (password = token)
app.MapGet("/nic/update", (HttpContext ctx, PushReceiver push) =>
{
    var token = PushReceiver.BasicAuthPassword(ctx.Request.Headers.Authorization.FirstOrDefault())
                ?? ctx.Request.Query["token"].FirstOrDefault();
    var ip = ctx.Request.Query["myip"].FirstOrDefault() ?? ctx.Request.Query["ip"].FirstOrDefault();
    var caller = ctx.Connection.RemoteIpAddress?.ToString();
    var (statusCode, body) = push.Update(token, ip, caller);
    return Results.Text(body, "text/plain", statusCode: statusCode);
}).AllowAnonymous();

app.Services.GetRequiredService<ConfigService>().EnsureLoaded();

app.Run();

public record PathOptions(string DataDir)
{
    public string ConfigFile => Path.Combine(DataDir, "config.json");
    public string LogFile => Path.Combine(DataDir, "log.txt");
}
