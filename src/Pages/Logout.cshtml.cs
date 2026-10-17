using Matddns.Platform;
using MatPlatform;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matddns.Pages;

// Logout is a harmless action; skip antiforgery so it works even from a stale tab.
// A session that began at MatIdentity (or was linked at the sign-in) ends there too; with MatIdentity the browser then lands on
// /Login?signedOut=1, so it is not signed in again right away. Without MatIdentity nothing changed.
[IgnoreAntiforgeryToken]
public class LogoutModel : PageModel
{
    private readonly MatPlatformSettings _platform;

    public LogoutModel(MatPlatformSettings platform) => _platform = platform;

    public async Task<IActionResult> OnGetAsync()
    {
        var idToken = MatddnsSession.IdTokenOf(User);
        MatIdentityPendingLink.Clear(HttpContext);
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        if (!_platform.IdentityEnabled)
            return Redirect("/"); // anon dashboard on -> shows it; off -> Index guard redirects to /Login

        return idToken is not null
            ? MatIdentitySignIn.SignOutAtIdentity(HttpContext, idToken)
            : Redirect("/Login?signedOut=1");
    }

    public Task<IActionResult> OnPostAsync() => OnGetAsync();
}
