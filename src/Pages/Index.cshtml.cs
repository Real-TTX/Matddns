using Matddns.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matddns.Pages;

[AllowAnonymous] // reachable without login; the handler enforces the AnonymousDashboard setting
public class IndexModel : PageModel
{
    private readonly StatusService _status;
    private readonly ConfigService _config;
    public IndexModel(StatusService status, ConfigService config)
    {
        _status = status;
        _config = config;
    }

    public StatusSnapshot Snapshot { get; private set; } = new();

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated != true && !_config.Read(c => c.Settings.AnonymousDashboard))
            return RedirectToPage("/Login");
        Snapshot = _status.Build(15);
        return Page();
    }
}
