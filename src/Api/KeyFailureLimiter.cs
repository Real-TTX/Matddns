using System.Collections.Concurrent;
using Matddns.Platform;
using MatPlatform;

namespace Matddns.Api;

/// <summary>
/// Counts the requests with a wrong key (answered <c>401</c>) per client address in a fixed window of one minute. After <see cref="PermitFailures"/>
/// of them the address is <see cref="IsBlocked">blocked</see> for the rest of the window: a request from it without a valid key or token is answered
/// <c>429</c> before the key is even looked at. A valid key is never limited, so Mattiverse with its key goes through however often it calls.
/// </summary>
public sealed class KeyFailureLimiter
{
    /// <summary>Wrong keys per address and minute before it is blocked.</summary>
    public const int DefaultPermitFailures = 10;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private const int PruneAbove = 10_000;

    private readonly ConcurrentDictionary<string, Counter> _counters = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public KeyFailureLimiter(int permitFailures = DefaultPermitFailures, TimeProvider? time = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(permitFailures, 1);
        PermitFailures = permitFailures;
        _time = time ?? TimeProvider.System;
    }

    public int PermitFailures { get; }

    /// <summary>Counts a wrong key from <paramref name="address"/>.</summary>
    public void Fail(string address)
    {
        var now = _time.GetUtcNow();
        if (_counters.Count > PruneAbove) Prune(now);
        _counters.AddOrUpdate(address,
            _ => new Counter(now, 1),
            (_, c) => now - c.Start >= Window ? new Counter(now, 1) : c with { Count = c.Count + 1 });
    }

    /// <summary>True while <paramref name="address"/> has had <see cref="PermitFailures"/> wrong keys within the current window.</summary>
    public bool IsBlocked(string address) => RetryAfter(address) is not null;

    /// <summary>How long <paramref name="address"/> is still blocked, or null when it is not.</summary>
    public TimeSpan? RetryAfter(string address)
    {
        if (!_counters.TryGetValue(address, out var c) || c.Count < PermitFailures) return null;
        var left = c.Start + Window - _time.GetUtcNow();
        return left > TimeSpan.Zero ? left : null;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (address, c) in _counters)
            if (now - c.Start >= Window) _counters.TryRemove(address, out _);
    }

    private sealed record Counter(DateTimeOffset Start, int Count);
}

/// <summary>The middleware of <see cref="KeyFailureLimiter"/> for the management API (<c>/api/v1</c>) and the protected platform endpoints.</summary>
public static class KeyFailureLimit
{
    /// <summary>
    /// Watches <c>/api/v1</c> and <c>/api/state</c>, <c>/api/configure</c>, <c>/api/actions</c> (the key part of the platform contract; the anonymous
    /// <c>/api</c> and <c>/api/health</c> and the token endpoints of the DynDNS receiver and the peers have their own request limits): every <c>401</c>
    /// counts against the address; a blocked address gets <c>429</c> with <c>Retry-After</c> unless the request carries a valid key (or token). The
    /// answers keep the shapes of the two APIs: <c>{"error": …}</c> for <c>/api/v1</c>, <c>{"errors": {"": …}}</c> for the platform.
    /// </summary>
    public static IApplicationBuilder UseKeyFailureLimit(this WebApplication app)
    {
        var engineKey = ManagementApi.EngineKey(app.Configuration);
        return app.Use(async (http, next) =>
        {
            var path = http.Request.Path;
            var engine = path.StartsWithSegments("/api/v1", StringComparison.OrdinalIgnoreCase);
            var platform = !engine && (path.StartsWithSegments("/api/state", StringComparison.OrdinalIgnoreCase)
                                       || path.StartsWithSegments("/api/configure", StringComparison.OrdinalIgnoreCase)
                                       || path.StartsWithSegments("/api/actions", StringComparison.OrdinalIgnoreCase));
            if (!engine && !platform)
            {
                await next(http);
                return;
            }

            var limiter = http.RequestServices.GetRequiredService<KeyFailureLimiter>();
            var address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (limiter.RetryAfter(address) is { } wait && !MonitoringCompatibility.IsAnonymousState(http.Request) && !await IsValidAsync(http, engine, engineKey))
            {
                const string message = "Too many requests with a wrong key. Try again later.";
                http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                await (engine
                    ? Results.Json(new { error = message }, statusCode: StatusCodes.Status429TooManyRequests)
                    : Results.Json(new { errors = new Dictionary<string, string> { [string.Empty] = message } }, statusCode: StatusCodes.Status429TooManyRequests))
                    .ExecuteAsync(http);
                return;
            }

            await next(http);
            if (http.Response.StatusCode == StatusCodes.Status401Unauthorized) limiter.Fail(address);
        });
    }

    private static async Task<bool> IsValidAsync(HttpContext http, bool engine, string? engineKey)
        => engine
            ? engineKey is not null && ManagementApi.KeyEquals(engineKey, http.Request.Headers[MatApiAccess.HeaderName].FirstOrDefault())
            : await http.RequestServices.GetRequiredService<MatApiAccess>().AuthenticateAsync(http) is not null;
}
