using Matddns.Models;
using Matddns.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matddns.Pages.Sources;

public class EditModel : PageModel
{
    private readonly ConfigService _config;
    private readonly UnifiClient _unifi;
    private readonly PublicIpClient _publicIp;

    public EditModel(ConfigService config, UnifiClient unifi, PublicIpClient publicIp)
    {
        _config = config;
        _unifi = unifi;
        _publicIp = publicIp;
    }

    public SourceGroup? Group { get; private set; }
    public bool IsNew => Group == null;

    [TempData] public string? Notice { get; set; }
    [TempData] public string? Error { get; set; }
    [TempData] public string? TestResult { get; set; }
    [TempData] public bool TestOk { get; set; }

    public IActionResult OnGet(string? id)
    {
        if (!string.IsNullOrEmpty(id))
        {
            Group = _config.Read(c => c.Sources.FirstOrDefault(g => g.Id == id));
            if (Group == null) { Error = "Gruppe nicht gefunden"; return RedirectToPage("Index"); }
        }
        return Page();
    }

    public IActionResult OnPostCreate(string Name, SourceKind Kind, int IntervalSeconds,
        string? UniUrl, string? UniSite, string? UniUser, string? UniPass, bool UniIgnoreCert = false)
    {
        if (string.IsNullOrWhiteSpace(Name)) { Error = "Name fehlt"; return RedirectToPage("Edit"); }

        var g = new SourceGroup
        {
            Name = Name.Trim(),
            Kind = Kind,
            IntervalSeconds = IntervalSeconds < 15 ? 60 : IntervalSeconds
        };
        if (Kind == SourceKind.Unifi)
        {
            g.Unifi = new UnifiSettings
            {
                BaseUrl = (UniUrl ?? "").Trim(),
                Site = string.IsNullOrWhiteSpace(UniSite) ? "default" : UniSite!.Trim(),
                Username = UniUser ?? "",
                Password = UniPass ?? "",
                IgnoreCertificate = UniIgnoreCert
            };
            // Einträge werden beim ersten erfolgreichen Abruf automatisch je WAN angelegt.
        }
        else
        {
            g.Entries.Add(new SourceEntry { Label = "Public IP" });
        }
        _config.Mutate(c => c.Sources.Add(g));
        Notice = Kind == SourceKind.Unifi
            ? "Angelegt – „Verbindung testen“ nutzen; die WAN‑Einträge erscheinen automatisch beim ersten Abruf."
            : "Angelegt.";
        return RedirectToPage("Edit", new { id = g.Id });
    }

    public IActionResult OnPostSave(string Id, string Name, int IntervalSeconds,
        string? UniUrl, string? UniSite, string? UniUser, string? UniPass, bool UniIgnoreCert = false)
    {
        _config.Mutate(c =>
        {
            var g = c.Sources.FirstOrDefault(x => x.Id == Id);
            if (g == null) return;
            if (!string.IsNullOrWhiteSpace(Name)) g.Name = Name.Trim();
            if (IntervalSeconds >= 15) g.IntervalSeconds = IntervalSeconds;
            if (g.Kind == SourceKind.Unifi)
            {
                g.Unifi ??= new UnifiSettings();
                g.Unifi.BaseUrl = (UniUrl ?? "").Trim();
                g.Unifi.Site = string.IsNullOrWhiteSpace(UniSite) ? "default" : UniSite!.Trim();
                g.Unifi.Username = UniUser ?? "";
                if (!string.IsNullOrEmpty(UniPass)) g.Unifi.Password = UniPass; // leer = unverändert
                g.Unifi.IgnoreCertificate = UniIgnoreCert;
            }
        });
        Notice = "Gespeichert";
        return RedirectToPage("Edit", new { id = Id });
    }

    public IActionResult OnPostDelete(string Id)
    {
        _config.Mutate(c =>
        {
            var g = c.Sources.FirstOrDefault(x => x.Id == Id);
            if (g == null) return;
            var entryIds = g.Entries.Select(e => e.Id).ToHashSet();
            c.Sources.Remove(g);
            foreach (var r in c.Rules)
                r.SourceEntryIdsInOrder.RemoveAll(id => entryIds.Contains(id));
        });
        Notice = "Source‑Gruppe entfernt";
        return RedirectToPage("Index");
    }

    public IActionResult OnPostAddEntry(string Id, string Label, string InterfaceName)
    {
        if (string.IsNullOrWhiteSpace(Label) || string.IsNullOrWhiteSpace(InterfaceName))
        {
            Error = "Label und Interface erforderlich";
            return RedirectToPage("Edit", new { id = Id });
        }
        _config.Mutate(c =>
        {
            var g = c.Sources.FirstOrDefault(x => x.Id == Id);
            g?.Entries.Add(new SourceEntry { Label = Label.Trim(), InterfaceName = InterfaceName.Trim() });
        });
        Notice = "Eintrag hinzugefügt";
        return RedirectToPage("Edit", new { id = Id });
    }

    public IActionResult OnPostDeleteEntry(string Id, string EntryId)
    {
        _config.Mutate(c =>
        {
            var g = c.Sources.FirstOrDefault(x => x.Id == Id);
            g?.Entries.RemoveAll(e => e.Id == EntryId);
            foreach (var r in c.Rules)
                r.SourceEntryIdsInOrder.RemoveAll(id => id == EntryId);
        });
        Notice = "Eintrag entfernt";
        return RedirectToPage("Edit", new { id = Id });
    }

    public IActionResult OnPostRefresh(string Id)
    {
        _config.Mutate(c =>
        {
            var g = c.Sources.FirstOrDefault(x => x.Id == Id);
            if (g == null) return;
            foreach (var e in g.Entries) e.LastChecked = null;
        });
        Notice = "Prüfung angestoßen – Status aktualisiert sich in wenigen Sekunden.";
        return RedirectToPage("Edit", new { id = Id });
    }

    public async Task<IActionResult> OnPostTestAsync(string Id, CancellationToken ct,
        string? UniUrl, string? UniSite, string? UniUser, string? UniPass, bool UniIgnoreCert = false)
    {
        var g = _config.Read(c => c.Sources.FirstOrDefault(x => x.Id == Id));
        if (g == null) { Error = "Gruppe nicht gefunden"; return RedirectToPage("Index"); }

        try
        {
            if (g.Kind == SourceKind.Unifi)
            {
                // gegen die aktuell eingegebenen Werte testen; leeres Passwort = gespeichertes verwenden
                var probe = new UnifiSettings
                {
                    BaseUrl = string.IsNullOrWhiteSpace(UniUrl) ? (g.Unifi?.BaseUrl ?? "") : UniUrl.Trim(),
                    Site = string.IsNullOrWhiteSpace(UniSite) ? (g.Unifi?.Site ?? "default") : UniSite.Trim(),
                    Username = string.IsNullOrWhiteSpace(UniUser) ? (g.Unifi?.Username ?? "") : UniUser,
                    Password = string.IsNullOrEmpty(UniPass) ? (g.Unifi?.Password ?? "") : UniPass,
                    IgnoreCertificate = UniIgnoreCert
                };
                var wans = await _unifi.GetWansAsync(probe, ct);
                if (wans.Count == 0)
                {
                    TestOk = false;
                    TestResult = "Verbindung & Login ok, aber keine WAN‑Interfaces gefunden. Prüfe Site‑Name.";
                }
                else
                {
                    TestOk = true;
                    TestResult = "OK – gefundene WANs: " +
                        string.Join(", ", wans.Select(w =>
                            $"{(string.IsNullOrEmpty(w.DisplayName) ? w.Name : w.DisplayName)} [{w.Name}{(string.IsNullOrEmpty(w.Ifname) ? "" : "/" + w.Ifname)}]={(w.Up ? (w.Ip ?? "kein Lease") : "down")}"));
                }
            }
            else
            {
                var ip = await _publicIp.GetPublicIpAsync(ct);
                TestOk = ip != null;
                TestResult = ip != null ? $"OK – Public IP: {ip}" : "Keine Public IP ermittelbar.";
            }
        }
        catch (Exception ex)
        {
            TestOk = false;
            TestResult = ex.Message;
        }
        return RedirectToPage("Edit", new { id = Id });
    }
}
