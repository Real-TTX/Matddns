using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Matddns.Platform;

/// <summary>
/// The session of Matddns: its cookie (<c>matddns.auth</c>) with the user name, and, for a sign-in through MatIdentity (or an account linked at the
/// sign-in), the ID token, which the sign-out hands to MatIdentity (<c>id_token_hint</c>). The local sign-in and the one through MatIdentity use this one place.
/// </summary>
public static class MatddnsSession
{
    /// <summary>The claim that carries the ID token of MatIdentity in the session.</summary>
    public const string IdTokenClaim = "matddns:id_token";

    public static Task StartAsync(HttpContext http, string username, string? idToken)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, username) };
        if (!string.IsNullOrEmpty(idToken)) claims.Add(new Claim(IdTokenClaim, idToken));
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        return http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });
    }

    /// <summary>The ID token of MatIdentity of the current session, or null (a local sign-in).</summary>
    public static string? IdTokenOf(ClaimsPrincipal user) => user.FindFirst(IdTokenClaim)?.Value;
}
