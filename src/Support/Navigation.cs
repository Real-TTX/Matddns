using System.Globalization;
using Matddns.Models;

namespace Matddns.Support;

/// <summary>
/// A badge at the right end of a menu item (SHELL-14): a count in a tone of the status colours (COLOR-03). <see cref="Label"/> is what a screen reader
/// says ("2 rules with a problem").
/// </summary>
public sealed record NavBadge(string Text, string Tone, string Label)
{
    public static NavBadge Count(int number, string tone, string label)
        => new(number > 99 ? "99+" : number.ToString(CultureInfo.InvariantCulture), tone, label);

    public string Classes => "mat-nav__badge" + (Tone == "accent" ? string.Empty : $" mat-nav__badge--{Tone}");
}

public sealed record NavItem(string Title, string Href, string Icon, NavBadge? Badge = null);

/// <summary>A group of items under a small heading (SHELL-02); a group without a title has no heading.</summary>
public sealed record NavGroup(string? Title, IReadOnlyList<NavItem> Items);

/// <summary>The menu of Matddns and which item belongs to the current address.</summary>
public static class Navigation
{
    /// <summary>The menu of a signed-in user. A visitor of the public dashboard (no sign-in) only sees the dashboard.</summary>
    public static IReadOnlyList<NavGroup> For(bool signedIn, int sourceProblems, int ruleProblems)
    {
        var overview = new NavGroup(null, [new("Dashboard", "/", "dashboard")]);
        if (!signedIn)
        {
            return [overview];
        }

        return
        [
            overview,
            new("Configure",
            [
                new("Sources", "/Sources", "network", sourceProblems > 0 ? NavBadge.Count(sourceProblems, "error", sourceProblems == 1 ? "source with a problem" : "sources with a problem") : null),
                new("Domains", "/Domains", "globe"),
                new("Rules", "/Rules", "route", ruleProblems > 0 ? NavBadge.Count(ruleProblems, "error", ruleProblems == 1 ? "rule with a problem" : "rules with a problem") : null),
            ]),
            new("Administration",
            [
                new("Users", "/Users", "users"),
                new("System", "/System", "settings"),
                new("Log", "/Log", "file-text"),
            ]),
        ];
    }

    /// <summary>How many source entries report an error and how many enabled rules have a problem (what the dashboard counts too).</summary>
    public static (int SourceProblems, int RuleProblems) Problems(AppConfig config)
    {
        var sources = config.Sources.SelectMany(s => s.Entries).Count(e => e.LastError != null);
        var rules = config.Rules.Count(r => r.Enabled && !r.Dynamic
            && (r.LastResult is "no source" or "target missing" || (r.LastResult?.StartsWith("err", StringComparison.OrdinalIgnoreCase) ?? false)));
        return (sources, rules);
    }

    /// <summary>
    /// The item that belongs to <paramref name="path"/>: an address that is the same, or the longest one the path continues
    /// (<c>/Sources/Edit</c> belongs to <c>/Sources</c>, <c>/Users/Edit</c> to <c>/Users</c>).
    /// </summary>
    public static string? ActiveHref(IEnumerable<NavGroup> menu, string path)
        => menu.SelectMany(g => g.Items).Select(i => i.Href)
            .Where(h => h == "/" ? path == "/" : path.Equals(h, StringComparison.OrdinalIgnoreCase) || path.StartsWith(h + "/", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(h => h.Length)
            .FirstOrDefault();
}
