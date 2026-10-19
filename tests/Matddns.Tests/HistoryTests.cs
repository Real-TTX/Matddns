using System.Net;
using Matddns.Models;
using Matddns.Services;

namespace Matddns.Tests;

/// <summary>An updater over a test environment whose HTTP calls go to <paramref name="http"/> (no background loop; the tests call <c>RunNowAsync</c>).</summary>
internal static class TestUpdater
{
    public static UpdaterService Create(TestEnvironment env, HttpMessageHandler http, NotificationService? notifications = null)
    {
        var f = new StubHttpClientFactory(http);
        return new UpdaterService(env.Config, env.Log, new PublicIpClient(f), new DnsLookupClient(), new FritzboxClient(), new UnifiClient(),
            new UnifiCloudClient(f), new MatddnsClient(f), Writer(f), new SourceResolver(), new ReachabilityChecker(), env.History, notifications ?? new NotificationService(env.Config, env.Log, f));
    }

    public static RecordWriter Writer(IHttpClientFactory f)
        => new(new DynDnsClient(f), new NetcupClient(f), new CloudflareClient(f), new HetznerClient(f), new GoDaddyClient(f), new MatddnsClient(f));

    /// <summary>A static source and a DynDNS group with one A record bound by a rule; returns the ids.</summary>
    public static (string SourceId, string RuleId) StaticToDynDns(TestEnvironment env, string ip)
    {
        var source = new SourceGroup
        {
            Name = "Office",
            Kind = SourceKind.Static,
            Static = new StaticSettings { Ip = ip },
            Entries = { new SourceEntry { Label = "Static IP", CurrentIp = ip, LastChecked = DateTime.UtcNow } }
        };
        var entry = new DomainEntry { Hostname = "home.example.com", Type = DnsRecordType.A };
        var group = new DomainGroup { Name = "DynDNS", Kind = DomainKind.DynDns, DynDns = new DynDnsSettings { UpdateUrl = "https://dyn.example.org/update?h={hostname}&ip={ipv4}" }, Entries = { entry } };
        var rule = new Rule { DomainGroupId = group.Id, DomainEntryId = entry.Id, SourceEntryIdsInOrder = { source.Entries[0].Id }, CnameTargets = { "" } };
        env.Config.Mutate(c => { c.Sources.Add(source); c.Domains.Add(group); c.Rules.Add(rule); });
        return (source.Id, rule.Id);
    }

    public static void SetStaticIp(TestEnvironment env, string sourceId, string ip)
        => env.Config.Mutate(c =>
        {
            var s = c.Sources.Single(x => x.Id == sourceId);
            s.Static!.Ip = ip;
            s.Entries[0].CurrentIp = ip;
        });
}

public class HistoryTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    public void Dispose() => _env.Dispose();

    [Fact]
    public void A_change_keeps_the_value_before_and_the_same_value_is_no_change()
    {
        var h = _env.History;
        Assert.NotNull(h.Record("r1", "home.example.com", "A", "203.0.113.1", "Office · WAN"));
        Assert.Null(h.Record("r1", "home.example.com", "A", "203.0.113.1", "Office · WAN"));
        var second = h.Record("r1", "home.example.com", "A", "203.0.113.2", "Office · LTE");

        Assert.Equal("203.0.113.1", second!.Previous);
        Assert.Equal(["203.0.113.2", "203.0.113.1"], h.Of("HOME.example.com", "A").Select(c => c.Value));
        Assert.Empty(h.Of("home.example.com", "AAAA"));
    }

    [Fact]
    public void The_history_survives_a_restart_and_keeps_only_the_newest_entries()
    {
        for (var i = 0; i < HistoryService.MaxEntries + 5; i++)
            _env.History.Record("r", "h.example.com", "A", $"198.51.{i / 250}.{i % 250}", null);

        var reloaded = new HistoryService(_env.Paths);

        Assert.Equal(HistoryService.MaxEntries, reloaded.All().Count);
        Assert.Equal(_env.History.All()[0], reloaded.All()[0]);
    }

    [Fact]
    public void An_empty_history_takes_the_updates_of_the_log_over_once()
    {
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var updates = new[]
        {
            new LogEntry(t, LogLevel.Update, "rule:A home.example.com", "A home.example.com -> 203.0.113.1 (src#0) : good 203.0.113.1"),
            new LogEntry(t.AddHours(1), LogLevel.Update, "rule:A home.example.com", "A home.example.com -> 203.0.113.1 (src#0) : nochg"),
            new LogEntry(t.AddHours(2), LogLevel.Update, "rule:A home.example.com", "A home.example.com -> 203.0.113.9 (src#1) : good"),
            new LogEntry(t.AddHours(3), LogLevel.Update, "push", "something else"),
        };

        Assert.Equal(2, _env.History.SeedFrom(updates));
        Assert.Equal("203.0.113.1", _env.History.All()[0].Previous);
        Assert.Equal(0, new HistoryService(_env.Paths).SeedFrom(updates)); // the file exists now
    }

    [Fact]
    public async Task The_updater_records_every_change_of_a_record_once()
    {
        var dyn = StubHandler.Text("good");
        var updater = TestUpdater.Create(_env, dyn);
        var (sourceId, _) = TestUpdater.StaticToDynDns(_env, "203.0.113.10");

        await updater.RunNowAsync(CancellationToken.None);
        await updater.RunNowAsync(CancellationToken.None); // unchanged: neither written nor recorded again
        TestUpdater.SetStaticIp(_env, sourceId, "203.0.113.11");
        await updater.RunNowAsync(CancellationToken.None);

        var changes = _env.History.Of("home.example.com", "A");
        Assert.Equal(["203.0.113.11", "203.0.113.10"], changes.Select(c => c.Value));
        Assert.Equal("Office · Static IP", changes[0].Source);
        Assert.Equal(2, dyn.Calls.Count);
    }

    [Fact]
    public async Task A_failed_write_is_not_a_change()
    {
        var updater = TestUpdater.Create(_env, new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("down") }));
        TestUpdater.StaticToDynDns(_env, "203.0.113.10");

        await updater.RunNowAsync(CancellationToken.None);

        Assert.Empty(_env.History.All());
        Assert.Equal(1, _env.Config.Read(c => c.Rules.Single().FailCount));
    }
}
