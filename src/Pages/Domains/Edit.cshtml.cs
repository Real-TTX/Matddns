using Matddns.Models;
using Matddns.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matddns.Pages.Domains;

public class EditModel : PageModel
{
    private readonly ConfigService _config;
    public EditModel(ConfigService config) => _config = config;

    public DomainGroup? Group { get; private set; }
    public bool IsNew => Group == null;

    [TempData] public string? Notice { get; set; }
    [TempData] public string? Error { get; set; }

    public IActionResult OnGet(string? id)
    {
        if (!string.IsNullOrEmpty(id))
        {
            Group = _config.Current.Domains.FirstOrDefault(g => g.Id == id);
            if (Group == null) { Error = "Domain group not found."; return RedirectToPage("Index"); }
        }
        return Page();
    }

    public IActionResult OnPostCreate(string Name, DomainKind Kind,
        string? DynUrl, string? DynUser, string? DynPass,
        string? NcCustomer, string? NcKey, string? NcPass, bool NcAllowDynamic = false,
        string? CfToken = null, bool CfAllowDynamic = false,
        string? HzToken = null, bool HzAllowDynamic = false,
        string? GdKey = null, string? GdSecret = null, bool GdAllowDynamic = false,
        string? MdUrl = null, string? MdToken = null)
    {
        if (string.IsNullOrWhiteSpace(Name)) { Error = "Enter a name."; return RedirectToPage("Edit"); }
        if (InvalidUrl(Kind, DynUrl, MdUrl) is { } badUrl) { Error = badUrl; return RedirectToPage("Edit"); }

        var g = new DomainGroup { Name = Name.Trim(), Kind = Kind };
        if (Kind == DomainKind.DynDns)
            g.DynDns = new DynDnsSettings { UpdateUrl = DynUrl ?? "", Username = DynUser ?? "", Password = DynPass ?? "" };
        else if (Kind == DomainKind.Cloudflare)
            g.Cloudflare = new CloudflareSettings { ApiToken = (CfToken ?? "").Trim(), AllowDynamic = CfAllowDynamic };
        else if (Kind == DomainKind.Hetzner)
            g.Hetzner = new HetznerSettings { ApiToken = (HzToken ?? "").Trim(), AllowDynamic = HzAllowDynamic };
        else if (Kind == DomainKind.GoDaddy)
            g.GoDaddy = new GoDaddySettings { ApiKey = (GdKey ?? "").Trim(), ApiSecret = (GdSecret ?? "").Trim(), AllowDynamic = GdAllowDynamic };
        else if (Kind == DomainKind.Matddns)
            g.Matddns = new MatddnsLinkSettings { BaseUrl = (MdUrl ?? "").Trim(), Token = (MdToken ?? "").Trim() };
        else
            g.Netcup = new NetcupSettings { CustomerNumber = NcCustomer ?? "", ApiKey = NcKey ?? "", ApiPassword = NcPass ?? "", AllowDynamic = NcAllowDynamic };

        _config.Mutate(c => c.Domains.Add(g));
        Notice = "Created. Add the records below.";
        return RedirectToPage("Edit", new { id = g.Id });
    }

    public IActionResult OnPostSave(string Id, string Name,
        string? DynUrl, string? DynUser, string? DynPass,
        string? NcCustomer, string? NcKey, string? NcPass, bool NcAllowDynamic = false,
        string? CfToken = null, bool CfAllowDynamic = false,
        string? HzToken = null, bool HzAllowDynamic = false,
        string? GdKey = null, string? GdSecret = null, bool GdAllowDynamic = false,
        string? MdUrl = null, string? MdToken = null)
    {
        var storedKind = _config.Read(c => c.Domains.FirstOrDefault(x => x.Id == Id)?.Kind);
        if (storedKind is { } k && InvalidUrl(k, DynUrl, MdUrl) is { } badUrl) { Error = badUrl; return RedirectToPage("Edit", new { id = Id }); }

        _config.Mutate(c =>
        {
            var g = c.Domains.FirstOrDefault(x => x.Id == Id);
            if (g == null) return;
            if (!string.IsNullOrWhiteSpace(Name)) g.Name = Name.Trim();
            foreach (var r in c.Rules.Where(r => r.DomainGroupId == g.Id)) r.FailCount = 0; // new credentials: start again at the shortest wait
            if (g.Kind == DomainKind.DynDns)
            {
                g.DynDns ??= new DynDnsSettings();
                g.DynDns.UpdateUrl = DynUrl ?? "";
                g.DynDns.Username = DynUser ?? "";
                if (!string.IsNullOrEmpty(DynPass)) g.DynDns.Password = DynPass; // empty = unchanged
            }
            else if (g.Kind == DomainKind.Cloudflare)
            {
                g.Cloudflare ??= new CloudflareSettings();
                if (!string.IsNullOrEmpty(CfToken)) g.Cloudflare.ApiToken = CfToken.Trim(); // empty = unchanged
                g.Cloudflare.AllowDynamic = CfAllowDynamic;
            }
            else if (g.Kind == DomainKind.Hetzner)
            {
                g.Hetzner ??= new HetznerSettings();
                if (!string.IsNullOrEmpty(HzToken)) g.Hetzner.ApiToken = HzToken.Trim(); // empty = unchanged
                g.Hetzner.AllowDynamic = HzAllowDynamic;
            }
            else if (g.Kind == DomainKind.GoDaddy)
            {
                g.GoDaddy ??= new GoDaddySettings();
                if (!string.IsNullOrEmpty(GdKey)) g.GoDaddy.ApiKey = GdKey.Trim(); // empty = unchanged
                if (!string.IsNullOrEmpty(GdSecret)) g.GoDaddy.ApiSecret = GdSecret.Trim(); // empty = unchanged
                g.GoDaddy.AllowDynamic = GdAllowDynamic;
            }
            else if (g.Kind == DomainKind.Matddns)
            {
                g.Matddns ??= new MatddnsLinkSettings();
                g.Matddns.BaseUrl = (MdUrl ?? "").Trim();
                if (!string.IsNullOrEmpty(MdToken)) g.Matddns.Token = MdToken.Trim(); // empty = unchanged
            }
            else
            {
                g.Netcup ??= new NetcupSettings();
                g.Netcup.CustomerNumber = NcCustomer ?? "";
                if (!string.IsNullOrEmpty(NcKey)) g.Netcup.ApiKey = NcKey.Trim(); // empty = unchanged
                if (!string.IsNullOrEmpty(NcPass)) g.Netcup.ApiPassword = NcPass; // empty = unchanged
                g.Netcup.AllowDynamic = NcAllowDynamic;
            }
        });
        Notice = "Saved";
        return RedirectToPage("Edit", new { id = Id });
    }

    // Error message if the address this kind of group calls is not an http(s) URL, otherwise null.
    private static string? InvalidUrl(DomainKind kind, string? dynUrl, string? mdUrl) => kind switch
    {
        DomainKind.DynDns when !InputCheck.IsHttpUrl(dynUrl) => "Enter the update URL, for example https://example.org/nic/update?hostname={hostname}&myip={ipv4}.",
        DomainKind.Matddns when !InputCheck.IsHttpUrl(mdUrl) => "Enter the address of the peer, for example https://dyndns.example.com.",
        _ => null
    };

    public IActionResult OnPostDelete(string Id)
    {
        _config.Mutate(c =>
        {
            c.Domains.RemoveAll(g => g.Id == Id);
            c.Rules.RemoveAll(r => r.DomainGroupId == Id);
        });
        Notice = "Domain group deleted.";
        return RedirectToPage("Index");
    }

    public IActionResult OnPostAddEntry(string Id, DnsRecordType Type,
        string? Hostname, string? RecordName, string? DomainZone)
    {
        var group = _config.Read(c => c.Domains.FirstOrDefault(x => x.Id == Id));
        if (group == null) { Error = "Domain group not found."; return RedirectToPage("Index"); }

        string fqdn;
        string? rec = null;
        string? zone = null;

        if (group.IsZoneBased)
        {
            zone = (DomainZone ?? "").Trim().TrimEnd('.');
            rec = string.IsNullOrWhiteSpace(RecordName) ? "@" : RecordName.Trim().TrimEnd('.');
            if (!InputCheck.IsHostname(zone)) { Error = "Enter the zone, for example example.com."; return RedirectToPage("Edit", new { id = Id }); }
            fqdn = (rec is "@" or "*" or "") ? zone : $"{rec}.{zone}";
        }
        else
        {
            fqdn = (Hostname ?? "").Trim().TrimEnd('.');
            if (Type == DnsRecordType.CNAME) Type = DnsRecordType.A; // DynDNS cannot set CNAME
        }
        if (!InputCheck.IsHostname(fqdn)) { Error = $"'{fqdn}' is not a valid hostname, for example home.example.com."; return RedirectToPage("Edit", new { id = Id }); }
        if (group.Entries.Any(e => e.Type == Type && e.Hostname.Equals(fqdn, StringComparison.OrdinalIgnoreCase)))
        { Error = $"{fqdn} ({Type}) is already in this group."; return RedirectToPage("Edit", new { id = Id }); }

        _config.Mutate(c =>
        {
            var g = c.Domains.FirstOrDefault(x => x.Id == Id);
            g?.Entries.Add(new DomainEntry { Hostname = fqdn, Type = Type, RecordName = rec, Domain = zone });
        });
        Notice = "Record added";
        return RedirectToPage("Edit", new { id = Id });
    }

    public IActionResult OnPostDeleteEntry(string Id, string EntryId)
    {
        _config.Mutate(c =>
        {
            var g = c.Domains.FirstOrDefault(x => x.Id == Id);
            g?.Entries.RemoveAll(e => e.Id == EntryId);
            c.Rules.RemoveAll(r => r.DomainEntryId == EntryId);
        });
        Notice = "Record removed";
        return RedirectToPage("Edit", new { id = Id });
    }
}
