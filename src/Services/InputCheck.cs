namespace Matddns.Services;

/// <summary>Checks shared by the forms and the management API for the addresses Matddns calls and the names it writes.</summary>
public static class InputCheck
{
    /// <summary>
    /// An absolute http(s) URL. Matddns only ever calls such addresses (no file:, ftp: or other schemes); private addresses stay
    /// allowed on purpose, because a FRITZ!Box, a UniFi console or a peer are usually on the local network.
    /// </summary>
    public static bool IsHttpUrl(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var u)
           && u.Scheme is "http" or "https"
           && !string.IsNullOrEmpty(u.Host);

    /// <summary>A DNS name such as <c>home.example.com</c> (letters, digits, '-', '_'; a trailing dot is ignored).</summary>
    public static bool IsHostname(string? value)
        => !string.IsNullOrWhiteSpace(value) && PushReceiver.IsValidHostname(value.Trim().TrimEnd('.').ToLowerInvariant());
}
