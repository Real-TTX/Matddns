using System.Security.Cryptography;
using System.Text;

namespace Matddns.Services;

public class AuthService
{
    /// <summary>A syntactically valid hash to verify against when a username doesn't exist, so the login path
    /// spends the same PBKDF2 work either way (defeats a username-enumeration timing oracle).</summary>
    public static readonly string DummyHash =
        "v1$" + Convert.ToBase64String(new byte[16]) + "$" + Convert.ToBase64String(new byte[32]);

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations: 100_000,
            HashAlgorithmName.SHA256,
            outputLength: 32);
        return $"v1${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string password, string stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 3 || parts[0] != "v1") return false;
        try
        {
            var salt = Convert.FromBase64String(parts[1]);
            var expected = Convert.FromBase64String(parts[2]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                salt,
                iterations: 100_000,
                HashAlgorithmName.SHA256,
                outputLength: 32);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch
        {
            return false;
        }
    }
}
