using Matddns.Platform;
using Matddns.Services;
using MatPlatform;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matddns.Pages;

/// <summary>
/// Sign-in with user name and password. With MatIdentity (MATIDENTITY_AUTHORITY) a visit goes straight on to MatIdentity; the local form stays
/// reachable at <c>?local=1</c> (the way in when MatIdentity is down). <c>?signedOut=1</c>: back from the sign-out, no automatic sign-in.
/// <c>?ssoError=1</c>: the sign-in at MatIdentity failed. <c>?denied=1</c>: Matddns refused the person. <c>?link=1</c>: the person of MatIdentity
/// matches an account that is not linked yet; only the sign-in of exactly that account with its password links it (<see cref="MatIdentityPendingLink"/>,
/// <see cref="UserService.LinkExternal"/>), another account links nothing, Cancel deletes the pending link.
/// </summary>
[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly ConfigService _config;
    private readonly AuthService _auth;
    private readonly LogService _log;
    private readonly UserService _users;
    private readonly MatPlatformSettings _platform;
    private readonly ILogger<LoginModel> _logger;

    public LoginModel(ConfigService config, AuthService auth, LogService log, UserService users, MatPlatformSettings platform, ILogger<LoginModel> logger)
    {
        _config = config;
        _auth = auth;
        _log = log;
        _users = users;
        _platform = platform;
        _logger = logger;
    }

    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    public string? Error { get; set; }

    /// <summary><c>?local=1</c>: the local form, also when MatIdentity is configured.</summary>
    public bool Local { get; private set; }

    /// <summary><c>?signedOut=1</c>: back from the sign-out, no automatic sign-in.</summary>
    public bool SignedOut { get; private set; }

    /// <summary><c>?ssoError=1</c>: the sign-in at MatIdentity failed, or MatIdentity cannot be reached.</summary>
    public bool SsoError { get; private set; }

    /// <summary><c>?denied=1</c>: MatIdentity signed the person in, Matddns refused them (several accounts match, or the match is linked with someone else).</summary>
    public bool Denied { get; private set; }

    /// <summary>The pending link with an account of this name, when MatIdentity sent the person here to prove it.</summary>
    public MatIdentityPendingLink? PendingLink { get; private set; }

    /// <summary><c>?link=1</c> without a pending link: it expired (10 minutes) or was cancelled.</summary>
    public bool LinkExpired { get; private set; }

    public bool IdentityEnabled => _platform.IdentityEnabled;

    public async Task<IActionResult> OnGetAsync(string? returnUrl = null)
    {
        Prepare();
        if (User.Identity?.IsAuthenticated == true) return SafeRedirect(returnUrl);

        // Automatic sign-in: straight on to MatIdentity, no local form in the way.
        if (IdentityEnabled && !Local && !SignedOut && !SsoError && !Denied)
        {
            try
            {
                await HttpContext.ChallengeAsync(MatIdentitySignIn.Scheme, new AuthenticationProperties { RedirectUri = SafeUrl(returnUrl) });
                return new EmptyResult();
            }
            catch (InvalidOperationException ex)
            {
                // MatIdentity cannot be reached (its configuration cannot be read): the local form is the way in.
                _logger.LogWarning(ex, "MatIdentity cannot be reached, showing the local sign-in");
                Local = true;
                SsoError = true;
            }
        }

        return Page();
    }

    // Only follow local return URLs — never an absolute/foreign one (open-redirect guard).
    private IActionResult SafeRedirect(string? returnUrl) => Redirect(SafeUrl(returnUrl));

    private string SafeUrl(string? returnUrl) => !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : "/";

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
    {
        // A posted form is the local sign-in; on an error it shows again.
        Prepare();
        Local = true;

        var user = _config.Read(c => c.Users.FirstOrDefault(u =>
            u.Username.Equals(Username, StringComparison.OrdinalIgnoreCase)));
        // verify unconditionally (dummy hash if the user doesn't exist) so the response time doesn't reveal valid usernames
        var passwordOk = _auth.Verify(Password, user?.PasswordHash ?? AuthService.DummyHash);
        if (user != null && passwordOk)
        {
            // an older (weaker) hash is upgraded now that the password is known
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

            // A pending link counts only for exactly the account that matched; it is deleted either way.
            string? idToken = null;
            if (MatIdentityPendingLink.TakeFor(HttpContext, user.Username) is { } pending
                && _users.LinkExternal(user.Username, new ExternalIdentity(pending.Subject, pending.Email, pending.Name, pending.PreferredUserName), out _))
            {
                idToken = pending.IdToken;
            }

            await MatddnsSession.StartAsync(HttpContext, user, idToken);
            _log.Log(Services.LogLevel.Info, "auth", $"login ok: {Username}");
            return SafeRedirect(returnUrl);
        }

        _log.Log(Services.LogLevel.Warn, "auth", $"login failed: {Username}");
        Error = "Invalid credentials";
        Password = "";
        return Page();
    }

    /// <summary>Cancel on <c>?link=1</c>: the account is not linked, the pending link is deleted.</summary>
    public IActionResult OnPostCancelLink()
    {
        MatIdentityPendingLink.Clear(HttpContext);
        return Redirect("/Login?local=1");
    }

    private void Prepare()
    {
        Local = QueryFlag("local");
        SignedOut = QueryFlag("signedOut");
        SsoError = QueryFlag("ssoError");
        Denied = QueryFlag("denied");
        PendingLink = MatIdentityPendingLink.Peek(HttpContext);
        LinkExpired = PendingLink is null && QueryFlag("link");
    }

    /// <summary>A switch in the address: <c>1</c> or <c>true</c>.</summary>
    private bool QueryFlag(string name) => Request.Query[name].ToString() is "1" or "true" or "True";
}
