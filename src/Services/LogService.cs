using System.Collections.Concurrent;

namespace Matddns.Services;

// order = severity (for the minimum-level filter): Debug < Info < Update < Warn < Error
public enum LogLevel { Debug, Info, Update, Warn, Error }

public record LogEntry(DateTime Timestamp, LogLevel Level, string Source, string Message);

public class LogService
{
    private readonly PathOptions _paths;
    private readonly ConfigService _config;
    private readonly ConcurrentQueue<LogEntry> _ring = new();
    private const int RingMax = 1000;
    private readonly object _fileLock = new();

    public LogService(PathOptions paths, ConfigService config)
    {
        _paths = paths;
        _config = config;
        if (File.Exists(paths.LogFile))
        {
            try
            {
                foreach (var line in File.ReadLines(paths.LogFile).TakeLast(RingMax))
                {
                    var entry = Parse(line);
                    if (entry != null) _ring.Enqueue(entry);
                }
            }
            catch { /* ignore */ }
        }
    }

    private LogLevel MinLevel()
    {
        try { return _config.Read(c => c.Settings.MinLogLevel); }
        catch { return LogLevel.Info; }
    }

    public void Log(LogLevel level, string source, string message)
    {
        // write filter: anything below the minimum level is not recorded at all (no spam).
        if ((int)level < (int)MinLevel()) return;

        var e = new LogEntry(DateTime.UtcNow, level, source, message);
        _ring.Enqueue(e);
        while (_ring.Count > RingMax && _ring.TryDequeue(out _)) { }

        var line = $"{e.Timestamp:yyyy-MM-ddTHH:mm:ssZ}\t{e.Level}\t{e.Source}\t{e.Message.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ')}";
        lock (_fileLock)
        {
            try
            {
                File.AppendAllText(_paths.LogFile, line + Environment.NewLine);
                var info = new FileInfo(_paths.LogFile);
                if (info.Length > 5_000_000)
                {
                    var backup = _paths.LogFile + ".1";
                    if (File.Exists(backup)) File.Delete(backup);
                    File.Move(_paths.LogFile, backup);
                }
            }
            catch { /* logging must not throw */ }
        }
    }

    /// <summary>Removes file entries older than the configured retention (days). 0 = unlimited.</summary>
    public void ApplyRetention()
    {
        int days;
        try { days = _config.Read(c => c.Settings.LogRetentionDays); }
        catch { return; }
        if (days <= 0) return;

        var cutoff = DateTime.UtcNow.AddDays(-days);
        lock (_fileLock)
        {
            try
            {
                if (!File.Exists(_paths.LogFile)) return;
                var kept = new List<string>();
                foreach (var line in File.ReadLines(_paths.LogFile))
                {
                    var e = Parse(line);
                    if (e == null || e.Timestamp >= cutoff) kept.Add(line);
                }
                var tmp = _paths.LogFile + ".tmp";
                File.WriteAllLines(tmp, kept);
                File.Move(tmp, _paths.LogFile, true);
            }
            catch { /* best effort */ }
        }
    }

    public long LogFileBytes()
    {
        try { return File.Exists(_paths.LogFile) ? new FileInfo(_paths.LogFile).Length : 0; }
        catch { return 0; }
    }

    public void Clear()
    {
        while (_ring.TryDequeue(out _)) { }
        lock (_fileLock)
        {
            try { File.WriteAllText(_paths.LogFile, ""); } catch { /* best effort */ }
        }
    }

    /// <summary>All entries of exactly one level from the retained log file, newest first.</summary>
    public IReadOnlyList<LogEntry> EntriesOfLevel(LogLevel level, int max = 1000)
    {
        var list = new List<LogEntry>();
        lock (_fileLock)
        {
            try
            {
                if (File.Exists(_paths.LogFile))
                    foreach (var line in File.ReadLines(_paths.LogFile))
                    {
                        var e = Parse(line);
                        if (e != null && e.Level == level) list.Add(e);
                    }
            }
            catch { /* best effort */ }
        }
        list.Reverse();
        return list.Count > max ? list.Take(max).ToList() : list;
    }

    public IReadOnlyList<LogEntry> Recent(int count = 500, LogLevel? min = null, int? withinDays = null)
    {
        IEnumerable<LogEntry> q = _ring.Reverse();
        if (min != null) q = q.Where(e => (int)e.Level >= (int)min.Value);
        if (withinDays is > 0)
        {
            var cutoff = DateTime.UtcNow.AddDays(-withinDays.Value);
            q = q.Where(e => e.Timestamp >= cutoff);
        }
        return q.Take(count).ToList();
    }

    private static LogEntry? Parse(string line)
    {
        var parts = line.Split('\t', 4);
        if (parts.Length < 4) return null;
        if (!DateTime.TryParse(parts[0], null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var ts)) return null;
        if (!Enum.TryParse<LogLevel>(parts[1], out var level)) level = LogLevel.Info;
        return new LogEntry(ts, level, parts[2], parts[3]);
    }
}
