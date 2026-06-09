using Matddns.Models;
using Matddns.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matddns.Pages.Domains;

public class IndexModel : PageModel
{
    private readonly ConfigService _config;
    public IndexModel(ConfigService config) => _config = config;

    public List<DomainGroup> Groups { get; private set; } = new();
    [Microsoft.AspNetCore.Mvc.TempData] public string? Notice { get; set; }
    [Microsoft.AspNetCore.Mvc.TempData] public string? Error { get; set; }

    public void OnGet() => Groups = _config.Read(c => c.Domains.ToList());
}
