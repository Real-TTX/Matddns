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
    public string BaseUrl { get; private set; } = "";

    [TempData] public string? Notice { get; set; }
    [TempData] public string? Error { get; set; }

    public IActionResult OnGet(string? id)
    {
        BaseUrl = $"{Request.Scheme}://{Request.Host}";
        if (!string.IsNullOrEmpty(id))
        {
            Group = _config.Read(c => c.Domains.FirstOrDefault(g => g.Id == id));
            if (Group == null) { Error = "Group not found"; return RedirectToPage("Index"); }
        }
        return Page();
    }

    public IActionResult OnPostCreate(string Name, DomainKind Kind,
        string? DynUrl, string? DynUser, string? DynPass,
        string? NcCustomer, string? NcKey, string? NcPass, bool NcAllowDynamic = false)
    {
        if (string.IsNullOrWhiteSpace(Name)) { Error = "Name missing"; return RedirectToPage("Edit"); }

        var g = new DomainGroup { Name = Name.Trim(), Kind = Kind };
        if (Kind == DomainKind.DynDns)
            g.DynDns = new DynDnsSettings { UpdateUrl = DynUrl ?? "", Username = DynUser ?? "", Password = DynPass ?? "" };
        else
            g.Netcup = new NetcupSettings { CustomerNumber = NcCustomer ?? "", ApiKey = NcKey ?? "", ApiPassword = NcPass ?? "", AllowDynamic = NcAllowDynamic };

        _config.Mutate(c => c.Domains.Add(g));
        Notice = "Group created – now add records";
        return RedirectToPage("Edit", new { id = g.Id });
    }

    public IActionResult OnPostSave(string Id, string Name,
        string? DynUrl, string? DynUser, string? DynPass,
        string? NcCustomer, string? NcKey, string? NcPass, bool NcAllowDynamic = false)
    {
        _config.Mutate(c =>
        {
            var g = c.Domains.FirstOrDefault(x => x.Id == Id);
            if (g == null) return;
            if (!string.IsNullOrWhiteSpace(Name)) g.Name = Name.Trim();
            if (g.Kind == DomainKind.DynDns)
            {
                g.DynDns ??= new DynDnsSettings();
                g.DynDns.UpdateUrl = DynUrl ?? "";
                g.DynDns.Username = DynUser ?? "";
                if (!string.IsNullOrEmpty(DynPass)) g.DynDns.Password = DynPass; // empty = unchanged
            }
            else
            {
                g.Netcup ??= new NetcupSettings();
                g.Netcup.CustomerNumber = NcCustomer ?? "";
                g.Netcup.ApiKey = NcKey ?? "";
                if (!string.IsNullOrEmpty(NcPass)) g.Netcup.ApiPassword = NcPass; // empty = unchanged
                g.Netcup.AllowDynamic = NcAllowDynamic;
            }
        });
        Notice = "Saved";
        return RedirectToPage("Edit", new { id = Id });
    }

    public IActionResult OnPostDelete(string Id)
    {
        _config.Mutate(c =>
        {
            c.Domains.RemoveAll(g => g.Id == Id);
            c.Rules.RemoveAll(r => r.DomainGroupId == Id);
        });
        Notice = "Group removed";
        return RedirectToPage("Index");
    }

    public IActionResult OnPostAddEntry(string Id, DnsRecordType Type,
        string? Hostname, string? RecordName, string? DomainZone)
    {
        var kind = _config.Read(c => c.Domains.FirstOrDefault(x => x.Id == Id)?.Kind);
        if (kind == null) { Error = "Group not found"; return RedirectToPage("Index"); }

        string fqdn;
        string? rec = null;
        string? zone = null;

        if (kind == DomainKind.Netcup)
        {
            zone = (DomainZone ?? "").Trim().TrimEnd('.');
            rec = string.IsNullOrWhiteSpace(RecordName) ? "@" : RecordName.Trim().TrimEnd('.');
            if (string.IsNullOrEmpty(zone)) { Error = "Domain/zone missing (e.g. h5x.de)"; return RedirectToPage("Edit", new { id = Id }); }
            fqdn = (rec is "@" or "*" or "") ? zone : $"{rec}.{zone}";
        }
        else
        {
            fqdn = (Hostname ?? "").Trim().TrimEnd('.');
            if (string.IsNullOrEmpty(fqdn)) { Error = "Hostname (FQDN) missing"; return RedirectToPage("Edit", new { id = Id }); }
            if (Type == DnsRecordType.CNAME) Type = DnsRecordType.A; // DynDNS cannot set CNAME
        }

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
