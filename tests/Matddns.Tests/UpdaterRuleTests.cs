using Matddns.Models;
using Matddns.Services;

namespace Matddns.Tests;

public class UpdaterRuleTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static Rule RuleRunSecondsAgo(int seconds, bool onChange, int intervalSeconds, string? lastResult = "ok: x", string? lastSignature = "sig") => new()
    {
        OnChange = onChange,
        IntervalSeconds = intervalSeconds,
        LastRun = Now.AddSeconds(-seconds),
        LastResult = lastResult,
        LastSourceSignature = lastSignature
    };

    [Fact]
    public void A_rule_that_never_ran_is_due()
    {
        Assert.True(UpdaterService.RuleDue(new Rule { LastRun = null }, Now, "sig"));
    }

    [Fact]
    public void On_change_rule_runs_only_when_the_signature_changed()
    {
        var rule = RuleRunSecondsAgo(10, onChange: true, intervalSeconds: 0);

        Assert.False(UpdaterService.RuleDue(rule, Now, "sig"));
        Assert.True(UpdaterService.RuleDue(rule, Now, "other"));
    }

    [Fact]
    public void Interval_rule_runs_when_the_interval_elapsed_even_without_a_change()
    {
        Assert.False(UpdaterService.RuleDue(RuleRunSecondsAgo(299, onChange: false, intervalSeconds: 300), Now, "sig"));
        Assert.True(UpdaterService.RuleDue(RuleRunSecondsAgo(300, onChange: false, intervalSeconds: 300), Now, "sig"));
    }

    [Fact]
    public void Interval_only_rule_ignores_a_signature_change()
    {
        var rule = RuleRunSecondsAgo(10, onChange: false, intervalSeconds: 300);

        Assert.False(UpdaterService.RuleDue(rule, Now, "other"));
    }

    [Fact]
    public void A_failed_rule_backs_off_for_30_seconds_even_if_a_source_changed()
    {
        Assert.False(UpdaterService.RuleDue(RuleRunSecondsAgo(10, true, 0, lastResult: "err: boom"), Now, "other"));
        Assert.True(UpdaterService.RuleDue(RuleRunSecondsAgo(31, true, 0, lastResult: "err: boom"), Now, "other"));
    }

    [Fact]
    public void Signature_lists_both_address_families_of_every_source_in_rule_order()
    {
        var config = new AppConfig();
        config.Sources.Add(new SourceGroup
        {
            Entries =
            {
                new SourceEntry { Id = "a", CurrentIp = "203.0.113.1", CurrentIpv6 = "2001:db8::1" },
                new SourceEntry { Id = "b", CurrentIp = "203.0.113.2" }
            }
        });
        var rule = new Rule { SourceEntryIdsInOrder = { "b", "a", "missing" } };

        Assert.Equal("203.0.113.2/-;203.0.113.1/2001:db8::1;-/-;", UpdaterService.SourceSignature(rule, config));
    }
}
