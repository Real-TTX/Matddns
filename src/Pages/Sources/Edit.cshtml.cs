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
    private readonly DnsLookupClient _dns;
    private readonly FritzboxClient _fritz;

    public EditModel(ConfigService config, UnifiClient unifi, PublicIpClient publicIp, DnsLookupClient dns, FritzboxClient fritz)
    {
        _config = config;
        _unifi = unifi;
        _publicIp = publicIp;
        _dns = dns;
        _fritz = fritz;
    }

    public SourceGroup? Group { get; private set; }
    public bool IsNew => Group == null;
    public string BaseUrl { get; private set; } = "";
    public Rule? DynamicRule { get; private set; }   // a dynamic rule pointing at this source, if any

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
            DynamicRule = _config.Read(c => c.Rules.FirstOrDefault(r => r.Dynamic && r.DynamicSourceId == id));
        }
        return Page();
    }

    public IActionResult OnPostCreate(string Name, SourceKind Kind, int IntervalSeconds,
        string? UniUrl, string? UniSite, string? UniUser, string? UniPass, bool UniIgnoreCert = false,
        string? StaticIp = null, string? StaticIpv6 = null, string? DnsHost = null,
        string? FbUrl = null, string? FbUser = null, string? FbPass = null, bool FbIgnoreCert = false)
    {
        if (string.IsNullOrWhiteSpace(Name)) { Error = "Name missing"; return RedirectToPage("Edit"); }

        var g = new SourceGroup
        {
            Name = Name.Trim(),
            Kind = Kind,
            // Push is push-driven (not polled) -> no interval; others clamp to a sane minimum.
            IntervalSeconds = Kind == SourceKind.Push ? 0 : (IntervalSeconds < 15 ? 60 : IntervalSeconds)
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
        else if (Kind == SourceKind.Dns)
        {
            var host = (DnsHost ?? "").Trim();
            g.Dns = new DnsSettings { Hostname = host };
            g.Entries.Add(new SourceEntry { Label = string.IsNullOrEmpty(host) ? "DNS lookup" : host });
        }
        else if (Kind == SourceKind.Fritzbox)
        {
            g.Fritzbox = new FritzboxSettings
            {
                BaseUrl = string.IsNullOrWhiteSpace(FbUrl) ? "http://fritz.box:49000" : FbUrl!.Trim(),
                Username = FbUser ?? "",
                Password = FbPass ?? "",
                IgnoreCertificate = FbIgnoreCert
            };
            g.Entries.Add(new SourceEntry { Label = "FRITZ!Box WAN" });
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
        string? StaticIp = null, string? StaticIpv6 = null, string? DnsHost = null,
        string? FbUrl = null, string? FbUser = null, string? FbPass = null, bool FbIgnoreCert = false)
    {
        _config.Mutate(c =>
        {
            var g = c.Sources.FirstOrDefault(x => x.Id == Id);
            if (g == null) return;
            if (!string.IsNullOrWhiteSpace(Name)) g.Name = Name.Trim();
            if (g.Kind == SourceKind.Push) g.IntervalSeconds = 0; // push-driven, never polled
            else if (IntervalSeconds >= 15) g.IntervalSeconds = IntervalSeconds;
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
            else if (g.Kind == SourceKind.Dns)
            {
                g.Dns ??= new DnsSettings();
                g.Dns.Hostname = (DnsHost ?? "").Trim();
                var e = g.Entries.FirstOrDefault();
                if (e == null) { e = new SourceEntry(); g.Entries.Add(e); }
                e.Label = string.IsNullOrEmpty(g.Dns.Hostname) ? "DNS lookup" : g.Dns.Hostname;
                e.LastChecked = null; // force a re-resolve on the next poll
            }
            else if (g.Kind == SourceKind.Fritzbox)
            {
                g.Fritzbox ??= new FritzboxSettings();
                g.Fritzbox.BaseUrl = string.IsNullOrWhiteSpace(FbUrl) ? "http://fritz.box:49000" : FbUrl!.Trim();
                g.Fritzbox.Username = FbUser ?? "";
                if (!string.IsNullOrEmpty(FbPass)) g.Fritzbox.Password = FbPass; // empty = unchanged
                g.Fritzbox.IgnoreCertificate = FbIgnoreCert;
                var e = g.Entries.FirstOrDefault();
                if (e != null) e.LastChecked = null; // re-fetch on next poll
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
        string? UniUrl, string? UniSite, string? UniUser, string? UniPass, bool UniIgnoreCert = false,
        string? DnsHost = null,
        string? FbUrl = null, string? FbUser = null, string? FbPass = null, bool FbIgnoreCert = false)
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
            else if (g.Kind == SourceKind.Dns)
            {
                var host = string.IsNullOrWhiteSpace(DnsHost) ? (g.Dns?.Hostname ?? "") : DnsHost.Trim();
                var (v4, v6) = await _dns.ResolveAsync(host, ct);
                TestOk = v4 != null || v6 != null;
                TestResult = TestOk
                    ? $"OK – {host} → {string.Join(" / ", new[] { v4, v6 }.Where(x => x != null))}"
                    : (string.IsNullOrWhiteSpace(host) ? "Enter a hostname first." : $"Could not resolve {host}.");
            }
            else if (g.Kind == SourceKind.Fritzbox)
            {
                var probe = new FritzboxSettings
                {
                    BaseUrl = string.IsNullOrWhiteSpace(FbUrl) ? (g.Fritzbox?.BaseUrl ?? "http://fritz.box:49000") : FbUrl.Trim(),
                    Username = string.IsNullOrWhiteSpace(FbUser) ? (g.Fritzbox?.Username ?? "") : FbUser,
                    Password = string.IsNullOrEmpty(FbPass) ? (g.Fritzbox?.Password ?? "") : FbPass,
                    IgnoreCertificate = FbIgnoreCert
                };
                var w = await _fritz.GetWanAsync(probe, ct);
                TestOk = w.Ipv4 != null || w.Ipv6 != null;
                TestResult = TestOk ? $"OK – WAN {string.Join(" / ", new[] { w.Ipv4, w.Ipv6 }.Where(x => x != null))}" : "Connected, but no WAN IP returned.";
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
