using System.Security.Cryptography;
using System.Text;
using Matddns.Models;
using Matddns.Services;

namespace Matddns.Tests;

public class AuthServiceTests
{
    private readonly AuthService _auth = new();

    private static string LegacyHash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 100_000, HashAlgorithmName.SHA256, 32);
        return $"v1${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    [Fact]
    public void Hash_verifies_only_the_original_password()
    {
        var hash = _auth.Hash("correct horse");

        Assert.True(_auth.Verify("correct horse", hash));
        Assert.False(_auth.Verify("wrong horse", hash));
    }

    [Fact]
    public void New_hashes_use_the_current_format_with_a_unique_salt()
    {
        var first = _auth.Hash("same password");
        var second = _auth.Hash("same password");

        Assert.StartsWith("v2$600000$", first);
        Assert.NotEqual(first, second);
        Assert.False(_auth.NeedsRehash(first));
    }

    [Fact]
    public void Legacy_v1_hash_still_verifies_and_is_flagged_for_upgrade()
    {
        var legacy = LegacyHash("old password");

        Assert.True(_auth.Verify("old password", legacy));
        Assert.False(_auth.Verify("other", legacy));
        Assert.True(_auth.NeedsRehash(legacy));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain-text")]
    [InlineData("v2$abc$AAAA$AAAA")]
    [InlineData("v2$0$AAAA$AAAA")]
    [InlineData("v1$!!!$!!!")]
    [InlineData("v3$1$AAAA$AAAA")]
    public void Malformed_hashes_never_verify_and_never_throw(string stored)
    {
        Assert.False(_auth.Verify("anything", stored));
        Assert.True(_auth.NeedsRehash(stored));
    }

    [Fact]
    public void Dummy_hash_is_well_formed_but_matches_nothing()
    {
        Assert.False(_auth.Verify("", AuthService.DummyHash));
        Assert.False(_auth.Verify("admin", AuthService.DummyHash));
        Assert.False(_auth.NeedsRehash(AuthService.DummyHash));
    }

    [Fact]
    public void Security_stamp_changes_when_the_password_changes()
    {
        var user = new UserAccount { Username = "u", PasswordHash = _auth.Hash("first password") };
        var before = AuthService.SecurityStamp(user);

        Assert.Equal(before, AuthService.SecurityStamp(user));

        user.PasswordHash = _auth.Hash("second password");
        Assert.NotEqual(before, AuthService.SecurityStamp(user));
    }
}
