using Matddns.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matddns.Pages;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly ConfigService _config;
    private readonly AuthService _auth;
    private readonly LogService _log;

    public LoginModel(ConfigService config, AuthService auth, LogService log)
    {
        _config = config;
        _auth = auth;
        _log = log;
    }

    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    public string? Error { get; set; }

    public IActionResult OnGet(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true) return SafeRedirect(returnUrl);
        return Page();
    }

    // Only follow local return URLs — never an absolute/foreign one (open-redirect guard).
    private IActionResult SafeRedirect(string? returnUrl)
        => !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? Redirect(returnUrl) : Redirect("/");

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        var user = _config.Current.Users.FirstOrDefault(u =>
            u.Username.Equals(Username, StringComparison.OrdinalIgnoreCase));
        // verify unconditionally (dummy hash if the user doesn't exist) so the response time doesn't reveal valid usernames
        var passwordOk = _auth.Verify(Password, user?.PasswordHash ?? AuthService.DummyHash);
        if (user != null && passwordOk)
        {
            if (_auth.NeedsRehash(user.PasswordHash))
            {
                var upgradedHash = _auth.Hash(Password);
                _config.Mutate(c =>
                {
                    var stored = c.Users.FirstOrDefault(u => u.Username == user.Username);
                    if (stored != null) stored.PasswordHash = upgradedHash;
                });
                user.PasswordHash = upgradedHash;
            }

            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                AuthService.CreatePrincipal(user),
                new AuthenticationProperties { IsPersistent = true });
            _log.Log(Services.LogLevel.Info, "auth", $"login ok: {Username}");
            return SafeRedirect(returnUrl);
        }

        _log.Log(Services.LogLevel.Warn, "auth", $"login failed: {Username}");
        Error = "Invalid credentials";
        Password = "";
        return Page();
    }
}
