using System.Text.Json;
using System.Text.Json.Nodes;
using Matddns.Services;
using MatPlatform;

namespace Matddns.Platform;

/// <summary>
/// Matddns served <c>/api/health</c> and <c>/api/state</c> for monitoring software long before the platform contract (MatDesign docs/PLATFORM.md)
/// took both paths. Both keep working for those monitors:
/// <list type="bullet">
/// <item><c>GET /api/health</c> (anonymous) is ONE answer for everybody: the fields of the contract (<c>status</c>, <c>app</c>, <c>version</c>, <c>time</c>,
/// <c>checks</c>) plus every old field (<c>sources</c>, <c>rules</c>, <c>ipChanges</c>). <c>status</c> is <c>ok</c> or <c>degraded</c> as before, and
/// <c>down</c> when config.json cannot be read. The HTTP status follows the contract: 200 for ok and degraded, 503 for down (the Docker HEALTHCHECK
/// must not mark a degraded Matddns unhealthy). It used to be 503 for degraded: a monitor that wants that adds <c>?strict=1</c> (503 unless ok).</item>
/// <item><c>GET /api/state</c> without credentials of the contract (no <c>X-Api-Key</c>, no bearer token) answers the old monitoring state, unchanged and
/// anonymous as before. With credentials it is the contract's state (<c>{time, values}</c>, 401 for a wrong key or token), answered by MatPlatform.</item>
/// </list>
/// </summary>
public static class MonitoringCompatibility
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Maps the merged <c>/api/health</c> before MatPlatform's (a lower order wins), anonymous and rate limited like the contract's.</summary>
    public static IEndpointConventionBuilder MapMonitoringHealth(this IEndpointRouteBuilder app)
        => app.MapGet("/api/health", Health)
            .AllowAnonymous()
            .RequireRateLimiting(MatPlatformEndpoints.AnonymousRateLimit)
            .WithOrder(-1);

    /// <summary>Answers an anonymous <c>GET /api/state</c> with the old monitoring state; everything else goes on to MatPlatform.</summary>
    public static IApplicationBuilder UseMonitoringState(this IApplicationBuilder app)
        => app.Use(async (http, next) =>
        {
            if (IsAnonymousState(http.Request))
            {
                await LegacyState(http.RequestServices.GetRequiredService<StatusService>()).ExecuteAsync(http);
                return;
            }

            await next(http);
        });

    /// <summary>A plain <c>GET /api/state</c> without <c>X-Api-Key</c> and without a bearer token: a monitor (a Basic login of a proxy in front does not count), not a caller of the contract.</summary>
    internal static bool IsAnonymousState(HttpRequest request)
        => HttpMethods.IsGet(request.Method)
           && string.Equals(request.Path.Value?.TrimEnd('/'), "/api/state", StringComparison.OrdinalIgnoreCase)
           && !request.Headers.ContainsKey(MatApiAccess.HeaderName)
           && !request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);

    /// <summary>The monitoring state exactly as Matddns answered it before the platform contract.</summary>
    internal static IResult LegacyState(StatusService status)
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
    }

    private static IResult Health(HttpContext http, StatusService status, ConfigService config, MatPlatformOptions options)
    {
        var s = status.Build(5);
        var call = new MatPlatformCall(http, options.LanguageOf(http), "anonymous");
        var checks = MatddnsPlatform.HealthChecks(call, s, config.SaveBlocked);
        var state = checks.Any(c => c.Status == "down") ? "down" : checks.Any(c => c.Status == "degraded") ? "degraded" : "ok";

        // The contract first (MatPlatform's own types and JSON), then the fields the monitors know.
        var body = JsonSerializer.SerializeToNode(
            new MatHealth(state, MatddnsPlatform.AppId, BuildInfo.Version, new DateTimeOffset(DateTime.SpecifyKind(s.Time, DateTimeKind.Utc)), checks),
            MatApiDefinition.Json)!.AsObject();
        body["sources"] = JsonSerializer.SerializeToNode(s.Sources, Web);
        body["rules"] = JsonSerializer.SerializeToNode(s.Rules, Web);
        body["ipChanges"] = JsonSerializer.SerializeToNode(new { s.IpChanges.Total, s.IpChanges.Last24h, s.IpChanges.Last7d, s.IpChanges.LastChange }, Web);

        var strict = http.Request.Query["strict"].ToString() is "1" or "true" or "True";
        var unhealthy = state == "down" || (strict && state != "ok");
        return Results.Json(body, statusCode: unhealthy ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK);
    }
}
