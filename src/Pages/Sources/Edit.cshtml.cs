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
    public string BaseUrl { get; private set; } = "";

    [TempData] public string? Notice { get; set; }
    [TempData] public string? Error { get; set; }
    [TempData] public string? TestResult { get; set; }
    [TempData] public bool TestOk { get; set; }

    public IActionResult OnGet(string? id)
    {
        BaseUrl = $"{Request.Scheme}://{Request.Host}";
        if (!string.IsNullOrEmpty(id))
        {
            Group = _config.Read(c => c.Sources.FirstOrDefault(g => g.Id == id));
            if (Group == null) { Error = "Group not found"; return RedirectToPage("Index"); }
        }
        return Page();
    }

    public IActionResult OnPostCreate(string Name, SourceKind Kind, int IntervalSeconds,
        string? UniUrl, string? UniSite, string? UniUser, string? UniPass, bool UniIgnoreCert = false,
        string? StaticIp = null, string? StaticIpv6 = null)
    {
        if (string.IsNullOrWhiteSpace(Name)) { Error = "Name missing"; return RedirectToPage("Edit"); }

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
            // WAN entries are created automatically on the first successful fetch.
        }
        else if (Kind == SourceKind.Static)
        {
            var ip = (StaticIp ?? "").Trim();
            var ipv6 = (StaticIpv6 ?? "").Trim();
            g.Static = new StaticSettings { Ip = ip, Ipv6 = ipv6 };
            g.Entries.Add(new SourceEntry
            {
                Label = "Static IP",
                CurrentIp = string.IsNullOrEmpty(ip) ? null : ip,
                CurrentIpv6 = string.IsNullOrEmpty(ipv6) ? null : ipv6,
                LastChecked = DateTime.UtcNow
            });
        }
        else if (Kind == SourceKind.Push)
        {
            g.Push = new PushSettings { Token = PushReceiver.NewToken() };
            g.Entries.Add(new SourceEntry { Label = "Pushed IP" });
        }
        else
        {
            g.Entries.Add(new SourceEntry { Label = "Public IP" });
        }
        _config.Mutate(c => c.Sources.Add(g));
        Notice = Kind switch
        {
            SourceKind.Unifi => "Created – use \"Test connection\"; the WAN entries appear automatically on first fetch.",
            SourceKind.Push => "Created – configure the update URL shown below in your device/router.",
            _ => "Created."
        };
        return RedirectToPage("Edit", new { id = g.Id });
    }

    public IActionResult OnPostRegenerateToken(string Id)
    {
        _config.Mutate(c =>
        {
            var g = c.Sources.FirstOrDefault(x => x.Id == Id);
            if (g?.Push != null) g.Push.Token = PushReceiver.NewToken();
        });
        Notice = "New token generated – update your device.";
        return RedirectToPage("Edit", new { id = Id });
    }

    public IActionResult OnPostSave(string Id, string Name, int IntervalSeconds,
        string? UniUrl, string? UniSite, string? UniUser, string? UniPass, bool UniIgnoreCert = false,
        string? StaticIp = null, string? StaticIpv6 = null)
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
                if (!string.IsNullOrEmpty(UniPass)) g.Unifi.Password = UniPass; // empty = unchanged
                g.Unifi.IgnoreCertificate = UniIgnoreCert;
            }
            else if (g.Kind == SourceKind.Static)
            {
                g.Static ??= new StaticSettings();
                g.Static.Ip = (StaticIp ?? "").Trim();
                g.Static.Ipv6 = (StaticIpv6 ?? "").Trim();
                var e = g.Entries.FirstOrDefault();
                if (e == null) { e = new SourceEntry { Label = "Static IP" }; g.Entries.Add(e); }
                e.CurrentIp = string.IsNullOrEmpty(g.Static.Ip) ? null : g.Static.Ip;
                e.CurrentIpv6 = string.IsNullOrEmpty(g.Static.Ipv6) ? null : g.Static.Ipv6;
                e.LastChecked = DateTime.UtcNow;
                e.LastError = null;
            }
        });
        Notice = "Saved";
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
        Notice = "Source group removed";
        return RedirectToPage("Index");
    }

    public IActionResult OnPostAddEntry(string Id, string Label, string InterfaceName)
    {
        if (string.IsNullOrWhiteSpace(Label) || string.IsNullOrWhiteSpace(InterfaceName))
        {
            Error = "Label and interface required";
            return RedirectToPage("Edit", new { id = Id });
        }
        _config.Mutate(c =>
        {
            var g = c.Sources.FirstOrDefault(x => x.Id == Id);
            g?.Entries.Add(new SourceEntry { Label = Label.Trim(), InterfaceName = InterfaceName.Trim() });
        });
        Notice = "Entry added";
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
        Notice = "Entry removed";
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
        Notice = "Check triggered – status updates in a few seconds.";
        return RedirectToPage("Edit", new { id = Id });
    }

    public async Task<IActionResult> OnPostTestAsync(string Id, CancellationToken ct,
        string? UniUrl, string? UniSite, string? UniUser, string? UniPass, bool UniIgnoreCert = false)
    {
        var g = _config.Read(c => c.Sources.FirstOrDefault(x => x.Id == Id));
        if (g == null) { Error = "Group not found"; return RedirectToPage("Index"); }

        try
        {
            if (g.Kind == SourceKind.Unifi)
            {
                // test against the currently entered values; empty password = use the stored one
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
                    TestResult = "Connection & login ok, but no WAN interfaces found. Check the site name.";
                }
                else
                {
                    TestOk = true;
                    TestResult = "OK – WANs found: " +
                        string.Join(", ", wans.Select(w =>
                            $"{(string.IsNullOrEmpty(w.DisplayName) ? w.Name : w.DisplayName)} [{w.Name}{(string.IsNullOrEmpty(w.Ifname) ? "" : "/" + w.Ifname)}]={(w.Up ? (w.Ip ?? "no lease") : "down")}"));
                }
            }
            else
            {
                var ip = await _publicIp.GetPublicIpAsync(ct);
                TestOk = ip != null;
                TestResult = ip != null ? $"OK – public IP: {ip}" : "Could not determine public IP.";
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
