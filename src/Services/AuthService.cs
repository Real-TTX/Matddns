using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Matddns.Models;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Matddns.Services;

/// <summary>
/// Password hashing (PBKDF2-HMAC-SHA256). Stored format: <c>v2$iterations$salt$hash</c>; the legacy
/// <c>v1$salt$hash</c> (fixed 100k iterations) is still verified and upgraded on the next successful login.
/// </summary>
public class AuthService
{
    public const int MinPasswordLength = 8;
    public const string SecurityStampClaim = "matddns:stamp";

    private const int CurrentIterations = 600_000; // OWASP recommendation for PBKDF2-HMAC-SHA256
    private const int LegacyIterations = 100_000;

    /// <summary>A valid hash to verify against when a username doesn't exist, so the login path spends
    /// the same PBKDF2 work either way (defeats a username-enumeration timing oracle).</summary>
    public static readonly string DummyHash = Format(CurrentIterations, new byte[16], new byte[32]);

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return Format(CurrentIterations, salt, Derive(password, salt, CurrentIterations));
    }

    public bool Verify(string password, string stored)
    {
        if (!TryParse(stored, out var iterations, out var salt, out var expected)) return false;
        return CryptographicOperations.FixedTimeEquals(expected, Derive(password, salt, iterations));
    }

    /// <summary>True if the hash uses an older format or cost and should be replaced after a successful login.</summary>
    public bool NeedsRehash(string stored) =>
        !TryParse(stored, out var iterations, out _, out _) || iterations < CurrentIterations;

    /// <summary>
    /// Fingerprint of the user's current password hash. It is stored in the session cookie, so changing the
    /// password (or deleting the user) ends all existing sessions of that account.
    /// </summary>
    public static string SecurityStamp(UserAccount user) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user.PasswordHash)))[..16];

    public static ClaimsPrincipal CreatePrincipal(UserAccount user)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(SecurityStampClaim, SecurityStamp(user))
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, outputLength: 32);

    private static string Format(int iterations, byte[] salt, byte[] hash) =>
        $"v2${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";

    private static bool TryParse(string? stored, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = hash = Array.Empty<byte>();
        if (string.IsNullOrEmpty(stored)) return false;

        var parts = stored.Split('$');
        try
        {
            if (parts is ["v1", var legacySalt, var legacyHash])
            {
                iterations = LegacyIterations;
                salt = Convert.FromBase64String(legacySalt);
                hash = Convert.FromBase64String(legacyHash);
                return true;
            }
            if (parts is ["v2", var cost, var newSalt, var newHash] && int.TryParse(cost, out iterations) && iterations > 0)
            {
                salt = Convert.FromBase64String(newSalt);
                hash = Convert.FromBase64String(newHash);
                return true;
            }
        }
        catch (FormatException) { }
        return false;
    }
}
