using System.Security.Claims;
using Matddns.Models;
using Matddns.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Matddns.Platform;

/// <summary>
/// The session of Matddns: its cookie (<c>matddns.auth</c>) with the user name and the security stamp of the account (a changed password or a deleted
/// account ends the session, see <see cref="AuthService.CreatePrincipal"/>), and, for a sign-in through MatIdentity (or an account linked at the
/// sign-in), the ID token, which the sign-out hands to MatIdentity (<c>id_token_hint</c>), and the session of MatIdentity (<c>sid</c>). Every session
/// carries the time it started, so that a back-channel logout of MatIdentity (<see cref="UserService.EndSessions"/>) ends it at its next request
/// (<see cref="ValidateAsync"/>). The local sign-in and the one through MatIdentity use this one place.
/// </summary>
public static class MatddnsSession
{
    /// <summary>The claim that carries the ID token of MatIdentity in the session.</summary>
    public const string IdTokenClaim = "matddns:id_token";

    /// <summary>The claim that carries the session of MatIdentity (its <c>sid</c>).</summary>
    public const string SessionIdClaim = "matddns:sid";

    /// <summary>The claim that carries the time the session started (Unix milliseconds).</summary>
    public const string StartedClaim = "matddns:started";

    public static Task StartAsync(HttpContext http, UserAccount user, string? idToken, string? sessionId = null)
    {
        var principal = AuthService.CreatePrincipal(user);
        var identity = (ClaimsIdentity)principal.Identity!;
        if (!string.IsNullOrEmpty(idToken)) identity.AddClaim(new Claim(IdTokenClaim, idToken));
        if (!string.IsNullOrEmpty(sessionId)) identity.AddClaim(new Claim(SessionIdClaim, sessionId));
        identity.AddClaim(new Claim(StartedClaim, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties { IsPersistent = true });
    }

    /// <summary>The ID token of MatIdentity of the current session, or null (a local sign-in).</summary>
    public static string? IdTokenOf(ClaimsPrincipal user) => user.FindFirst(IdTokenClaim)?.Value;

    /// <summary>
    /// The check of the cookie at every request: the session ends (signed out) when its account is gone, its password changed (security stamp), or
    /// MatIdentity ended it (<see cref="UserService.IsSessionEnded"/>).
    /// </summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext ctx)
    {
        var principal = ctx.Principal;
        var name = principal?.Identity?.Name;
        var stamp = principal?.FindFirst(AuthService.SecurityStampClaim)?.Value;
        var sessionId = principal?.FindFirst(SessionIdClaim)?.Value;
        DateTimeOffset? started = long.TryParse(principal?.FindFirst(StartedClaim)?.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : null;

        var config = ctx.HttpContext.RequestServices.GetRequiredService<ConfigService>();
        var valid = config.Read(c =>
            c.Users.FirstOrDefault(u => u.Username.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } user
            && AuthService.SecurityStamp(user) == stamp
            && !UserService.IsSessionEnded(user, sessionId, started));
        if (!valid)
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }
}
