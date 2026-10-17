using Matddns.Services;
using MatPlatform;

namespace Matddns.Platform;

/// <summary>
/// Matddns' side of the sign-in through MatIdentity (MatPlatform <see cref="IMatIdentityUsers"/>): <see cref="UserService.SignInExternal"/> finds the
/// account by <c>sub</c> (or creates one), and the session starts with the ID token for the sign-out at MatIdentity. An account whose name matches the
/// person but is not linked yet is not entered: the answer is <see cref="MatIdentitySignInResult.LinkRequired"/>, the person signs in once with its
/// password (<c>/Login?local=1&amp;link=1</c>) and the sign-in page links it (<see cref="UserService.LinkExternal"/>).
/// Matddns has no roles (every account may do everything), so the claim <c>role</c> is not read.
/// </summary>
public sealed class MatddnsIdentityUsers(UserService users, ConfigService config) : IMatIdentityUsers
{
    public async Task<MatIdentitySignInResult> SignInAsync(MatIdentityPerson person, HttpContext http, CancellationToken ct)
    {
        var result = users.SignInExternal(new ExternalIdentity(person.Subject, person.Email, person.Name, person.PreferredUserName));

        if (result.LinkUsername is { } link) return MatIdentitySignInResult.LinkRequired(link);
        if (result.Username is not { } username) return MatIdentitySignInResult.Refused(result.Error ?? "refused");

        var account = config.Read(c => c.Users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase)));
        if (account is null) return MatIdentitySignInResult.Refused("The account does not exist any more.");

        await MatddnsSession.StartAsync(http, account, person.IdToken);
        return MatIdentitySignInResult.Success();
    }
}
