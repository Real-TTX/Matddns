using System.Net;
using System.Text.Json;
using Matddns.Api;

namespace Matddns.Tests;

/// <summary>A clock the tests move by hand.</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
}

public class KeyFailureLimiterTests
{
    [Fact]
    public void An_address_is_blocked_after_the_permitted_failures_until_the_window_ends()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero));
        var limiter = new KeyFailureLimiter(3, time);

        limiter.Fail("198.51.100.1");
        limiter.Fail("198.51.100.1");
        Assert.False(limiter.IsBlocked("198.51.100.1"));
        limiter.Fail("198.51.100.1");
        Assert.True(limiter.IsBlocked("198.51.100.1"));
        Assert.False(limiter.IsBlocked("198.51.100.2")); // per address

        time.Now += TimeSpan.FromSeconds(45);
        Assert.Equal(TimeSpan.FromSeconds(15), limiter.RetryAfter("198.51.100.1"));

        time.Now += TimeSpan.FromSeconds(15);
        Assert.False(limiter.IsBlocked("198.51.100.1"));
        limiter.Fail("198.51.100.1"); // a new window starts with one
        Assert.False(limiter.IsBlocked("198.51.100.1"));
    }
}

/// <summary>Matddns with the real limit of wrong keys (the other factories lift it, they send wrong keys on purpose).</summary>
public sealed class KeyLimitFactory() : MatddnsFactory(platformKey: true, engineKey: true, authority: null, keyLimit: true);

public sealed class KeyLimitTests(KeyLimitFactory site) : IClassFixture<KeyLimitFactory>
{
    [Fact]
    public async Task Wrong_keys_get_429_after_ten_and_a_valid_key_never_does()
    {
        for (var i = 0; i < KeyFailureLimiter.DefaultPermitFailures; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await site.Api("wrong-key").GetAsync("/api/v1/status")).StatusCode);

        // the engine API: 429 in its own shape, with Retry-After
        var limited = await site.Api("wrong-key").GetAsync("/api/v1/status");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.InRange(int.Parse(limited.Headers.GetValues("Retry-After").Single()), 1, 60);
        using (var doc = JsonDocument.Parse(await limited.Content.ReadAsStringAsync()))
            Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("error").GetString()));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await site.Anonymous().GetAsync("/api/v1/sources")).StatusCode);

        // the key part of the platform API: the same address is blocked there too, in the platform's shape
        var platform = await site.Api("wrong-key").GetAsync("/api/configure");
        Assert.Equal(HttpStatusCode.TooManyRequests, platform.StatusCode);
        using (var doc = JsonDocument.Parse(await platform.Content.ReadAsStringAsync()))
            Assert.True(doc.RootElement.GetProperty("errors").TryGetProperty("", out _));

        // valid keys go through, however blocked the address is
        for (var i = 0; i < 20; i++)
            Assert.Equal(HttpStatusCode.OK, (await site.Api(MatddnsFactory.EngineKey).GetAsync("/api/v1/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await site.Api().GetAsync("/api/configure")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await site.Api().GetAsync("/api/state")).StatusCode);

        // the anonymous monitoring endpoints are not key attempts
        Assert.Equal(HttpStatusCode.OK, (await site.Anonymous().GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await site.Anonymous().GetAsync("/api/health")).StatusCode);
    }
}
