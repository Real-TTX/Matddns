using Matddns.Models;

namespace Matddns.Services;

/// <summary>A person MatIdentity signed in, as Matddns sees it (the claims <c>sub</c>, <c>email</c>, <c>name</c>, <c>preferred_username</c>).</summary>
public sealed record ExternalIdentity(string Subject, string? Email, string? Name, string? PreferredUserName);

/// <summary>
/// The answer to a person of MatIdentity: <see cref="Username"/> = sign in as this account; <see cref="LinkUsername"/> = an unlinked account matches
/// and has to be proven by its password first (nothing was changed); else <see cref="Error"/> (refused).
/// </summary>
public sealed record ExternalSignInResult(string? Username, string? LinkUsername, string? Error)
{
    public static ExternalSignInResult SignIn(string username) => new(username, null, null);

    public static ExternalSignInResult Link(string username) => new(null, username, null);

    public static ExternalSignInResult Refused(string error) => new(null, null, error);
}

/// <summary>
/// The accounts of Matddns and MatIdentity (MatDesign docs/PLATFORM.md, "The person"). Matddns has no e-mail column and no roles (every account
/// may do everything), so an account matches a person by its user name: equal to <c>preferred_username</c> or to the e-mail address.
/// A match is never linked by the name alone (anyone could have created an account with that name): the person has to sign in once with the
/// account's password (<see cref="LinkExternal"/>). Everything is written to the log.
/// </summary>
public class UserService
{
    private readonly ConfigService _config;
    private readonly LogService _log;

    public UserService(ConfigService config, LogService log)
    {
        _config = config;
        _log = log;
    }

    /// <summary>
    /// Finds the account of <paramref name="person"/> by its subject; else answers <see cref="ExternalSignInResult.Link"/> for exactly one unlinked account
    /// whose name matches; refuses several matches or a match linked with another subject; else creates an account (no password) with a free name.
    /// </summary>
    public ExternalSignInResult SignInExternal(ExternalIdentity person)
    {
        if (string.IsNullOrWhiteSpace(person.Subject)) return ExternalSignInResult.Refused("MatIdentity sent no subject.");

        string? created = null;
        var result = _config.Transact(c =>
        {
            var linked = c.Users.FirstOrDefault(u => u.ExternalSubject == person.Subject);
            if (linked != null) return ExternalSignInResult.SignIn(linked.Username);

            var names = new[] { person.PreferredUserName, person.Email }
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!.Trim())
                .ToList();
            var matches = c.Users.Where(u => names.Any(n => n.Equals(u.Username, StringComparison.OrdinalIgnoreCase))).ToList();
            if (matches.Count > 1) return ExternalSignInResult.Refused("Several accounts match this person.");
            if (matches.Count == 1)
            {
                return matches[0].ExternalSubject == null
                    ? ExternalSignInResult.Link(matches[0].Username)
                    : ExternalSignInResult.Refused($"The account '{matches[0].Username}' is linked with another person of MatIdentity.");
            }

            var name = FreeName(c, BaseName(person));
            c.Users.Add(new UserAccount { Username = name, PasswordHash = "", ExternalSubject = person.Subject });
            created = name;
            return ExternalSignInResult.SignIn(name);
        });

        if (created != null) _log.Log(LogLevel.Info, "auth", $"user '{created}' created through MatIdentity");
        if (result.Username != null) _log.Log(LogLevel.Info, "auth", $"login ok (MatIdentity): {result.Username}");
        else if (result.LinkUsername != null) _log.Log(LogLevel.Info, "auth", $"MatIdentity: the account '{result.LinkUsername}' has to be linked by its password first");
        else _log.Log(LogLevel.Warn, "auth", $"login refused (MatIdentity): {result.Error}");
        return result;
    }

    /// <summary>
    /// After the local sign-in of <paramref name="username"/> (its password proven) with a pending link for exactly this account: links it with
    /// <paramref name="person"/>. Refused when the account is gone, is linked with another subject, or another account has the subject.
    /// </summary>
    public bool LinkExternal(string username, ExternalIdentity person, out string? error)
    {
        string? problem = null;
        _config.Transact(c =>
        {
            var user = c.Users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (user == null) problem = "The account does not exist any more.";
            else if (user.ExternalSubject != null && user.ExternalSubject != person.Subject) problem = "The account is linked with another person of MatIdentity.";
            else if (c.Users.Any(u => u != user && u.ExternalSubject == person.Subject)) problem = "Another account is linked with this person of MatIdentity.";
            else user.ExternalSubject = person.Subject;
            return 0;
        });

        error = problem;
        if (problem == null) _log.Log(LogLevel.Info, "auth", $"user '{username}' linked with MatIdentity");
        else _log.Log(LogLevel.Warn, "auth", $"user '{username}' not linked with MatIdentity: {problem}");
        return problem == null;
    }

    private static string BaseName(ExternalIdentity person)
    {
        var name = person.PreferredUserName;
        if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(person.Email)) name = person.Email.Split('@')[0];
        if (string.IsNullOrWhiteSpace(name)) name = person.Name;
        name = (name ?? "").Trim();
        return name.Length == 0 ? "user" : name.Length > 64 ? name[..64] : name;
    }

    private static string FreeName(AppConfig c, string name)
    {
        bool Taken(string n) => c.Users.Any(u => u.Username.Equals(n, StringComparison.OrdinalIgnoreCase));
        if (!Taken(name)) return name;
        for (var i = 2; ; i++)
            if (!Taken($"{name}-{i}")) return $"{name}-{i}";
    }
}
