using Matddns.Models;

namespace Matddns.Services;

public class SourceResolver
{
    public record ResolvedEntry(SourceGroup Group, SourceEntry Entry);

    public IEnumerable<ResolvedEntry> AllEntries(AppConfig cfg)
    {
        foreach (var g in cfg.Sources)
            foreach (var e in g.Entries)
                yield return new ResolvedEntry(g, e);
    }

    public ResolvedEntry? Find(AppConfig cfg, string entryId)
        => AllEntries(cfg).FirstOrDefault(r => r.Entry.Id == entryId);
}
