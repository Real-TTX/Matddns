using System.Text.Json;

namespace Matddns.Services;

/// <summary>One change of the value of a record: what was written, what it was before and from which source.</summary>
/// <param name="RuleId">The rule that wrote it (for a dynamic receiver its dynamic rule).</param>
/// <param name="Record">The full name of the record (<c>home.example.com</c>).</param>
/// <param name="Type">A, AAAA or CNAME.</param>
/// <param name="Previous">The value before, null for the first write Matddns knows of.</param>
/// <param name="Source">"Source · entry" the value came from.</param>
public sealed record IpChange(DateTime Time, string RuleId, string Record, string Type, string Value, string? Previous, string? Source);

/// <summary>
/// The history of IP changes of every record, in <c>/data/history.jsonl</c> (one JSON object per line). It is kept apart from the log, so neither the
/// minimum log level, the retention of the log nor clearing it loses it. The newest <see cref="MaxEntries"/> changes are kept.
/// </summary>
public partial class HistoryService
{
    public const int MaxEntries = 2000;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _file;
    private readonly object _lock = new();
    private readonly List<IpChange> _entries = new();

    public HistoryService(PathOptions paths)
    {
        _file = Path.Combine(paths.DataDir, "history.jsonl");
        try
        {
            if (File.Exists(_file))
                foreach (var line in File.ReadLines(_file))
                {
                    try
                    {
                        if (JsonSerializer.Deserialize<IpChange>(line, Json) is { } e) _entries.Add(e);
                    }
                    catch (JsonException) { /* skip a broken line */ }
                }
        }
        catch (IOException) { /* start empty */ }
        if (_entries.Count > MaxEntries) _entries.RemoveRange(0, _entries.Count - MaxEntries);
    }

    /// <summary>
    /// Records that <paramref name="value"/> was written to a record, unless it is the value that record had last (a forced rewrite is no change).
    /// Returns the change, or null when nothing changed.
    /// </summary>
    public IpChange? Record(string ruleId, string record, string type, string value, string? source, DateTime? time = null)
    {
        lock (_lock)
        {
            var last = _entries.LastOrDefault(e => e.Record.Equals(record, StringComparison.OrdinalIgnoreCase) && e.Type == type);
            if (last?.Value == value) return null;

            var change = new IpChange(time ?? DateTime.UtcNow, ruleId, record, type, value, last?.Value, source);
            _entries.Add(change);
            try
            {
                if (_entries.Count > MaxEntries)
                {
                    _entries.RemoveRange(0, _entries.Count - MaxEntries);
                    var tmp = _file + ".tmp";
                    File.WriteAllLines(tmp, _entries.Select(e => JsonSerializer.Serialize(e, Json)));
                    File.Move(tmp, _file, true);
                }
                else
                {
                    File.AppendAllText(_file, JsonSerializer.Serialize(change, Json) + Environment.NewLine);
                }
            }
            catch (IOException) { /* the change stays in memory */ }
            return change;
        }
    }

    /// <summary>The changes of one record (<paramref name="record"/> of <paramref name="type"/>), newest first.</summary>
    public IReadOnlyList<IpChange> Of(string record, string type, int max = 20)
    {
        lock (_lock)
            return _entries.Where(e => e.Type == type && e.Record.Equals(record, StringComparison.OrdinalIgnoreCase)).Reverse().Take(max).ToList();
    }

    /// <summary>The changes written for <paramref name="ruleId"/> (a dynamic receiver writes many records), newest first.</summary>
    public IReadOnlyList<IpChange> OfRule(string ruleId, int max = 20)
    {
        lock (_lock) return _entries.Where(e => e.RuleId == ruleId).Reverse().Take(max).ToList();
    }

    /// <summary>
    /// Fills an empty history once from the updates in the log (an install from before the history): <c>A home.example.com -> 203.0.113.7 (src#0) : ...</c>.
    /// Returns how many changes it took over.
    /// </summary>
    public int SeedFrom(IEnumerable<LogEntry> updatesOldestFirst)
    {
        lock (_lock)
        {
            if (_entries.Count > 0 || File.Exists(_file)) return 0;
            var taken = 0;
            foreach (var u in updatesOldestFirst)
            {
                var m = LogLine().Match(u.Message);
                if (!m.Success) continue;
                var (type, record, value) = (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
                var last = _entries.LastOrDefault(e => e.Type == type && e.Record.Equals(record, StringComparison.OrdinalIgnoreCase));
                if (last?.Value == value) continue;
                _entries.Add(new IpChange(u.Timestamp, "", record, type, value, last?.Value, null));
                taken++;
            }
            if (_entries.Count > MaxEntries) _entries.RemoveRange(0, _entries.Count - MaxEntries);
            try { File.WriteAllLines(_file, _entries.Select(e => JsonSerializer.Serialize(e, Json))); }
            catch (IOException) { /* stays in memory */ }
            return taken;
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(A|AAAA|CNAME) (\S+) -> (\S+) ")]
    private static partial System.Text.RegularExpressions.Regex LogLine();

    /// <summary>All changes, newest first (for the counts of the dashboard and of <c>/api/state</c>).</summary>
    public IReadOnlyList<IpChange> All()
    {
        lock (_lock) return _entries.AsEnumerable().Reverse().ToList();
    }
}
