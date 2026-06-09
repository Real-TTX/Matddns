using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matddns.Pages;

public class IndexModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Domains/Index");
}
