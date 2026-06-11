using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matddns.Pages;

// Logout is a harmless action; skip antiforgery so it works even from a stale tab.
[IgnoreAntiforgeryToken]
public class LogoutModel : PageModel
{
    public async Task<IActionResult> OnGetAsync()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Redirect("/"); // anon dashboard on -> shows it; off -> Index guard redirects to /Login
    }

    public Task<IActionResult> OnPostAsync() => OnGetAsync();
}
