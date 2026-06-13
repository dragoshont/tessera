namespace Tessera.Broker.Egress;

/// <summary>
/// The SSRF allow-list guard for the injection egress (architecture §6). The
/// broker may only inject a credential into an upstream whose host is explicitly
/// allow-listed — critical for an egress proxy. Empty allow-list ⇒ nothing allowed.
/// </summary>
public sealed class SsrfGuard
{
    private readonly HashSet<string> _allowedHosts;

    /// <summary>Creates a guard over the configured allow-list of hostnames.</summary>
    public SsrfGuard(IEnumerable<string> allowedHosts) =>
        _allowedHosts = new HashSet<string>(allowedHosts, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="destination"/> is an absolute https URL to an allow-listed host.</summary>
    public bool IsAllowed(string destination) =>
        Uri.TryCreate(destination, UriKind.Absolute, out var uri) && IsAllowed(uri);

    /// <summary>True when <paramref name="uri"/> is https and its host is allow-listed.</summary>
    public bool IsAllowed(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        // Only https, only allow-listed hosts. No raw IPs unless explicitly listed.
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return _allowedHosts.Contains(uri.Host);
    }
}
