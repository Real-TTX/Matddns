using System.Security.Claims;
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
        var user = _config.Read(c => c.Users.FirstOrDefault(u =>
            u.Username.Equals(Username, StringComparison.OrdinalIgnoreCase)));
        if (user != null && _auth.Verify(Password, user.PasswordHash))
        {
            var claims = new[] { new Claim(ClaimTypes.Name, user.Username) };
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity),
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
