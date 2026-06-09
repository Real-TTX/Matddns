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
app.MapGet("/healthz", () => "ok").AllowAnonymous();

app.Services.GetRequiredService<ConfigService>().EnsureLoaded();

app.Run();

public record PathOptions(string DataDir)
{
    public string ConfigFile => Path.Combine(DataDir, "config.json");
    public string LogFile => Path.Combine(DataDir, "log.txt");
}
