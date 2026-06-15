namespace Tessera.Core.Egress;

/// <summary>
/// The SSRF allow-list guard for the injection egress (architecture §6). The
/// broker may only inject a credential into an upstream whose host is explicitly
/// allow-listed — critical for an egress proxy. Empty allow-list ⇒ nothing allowed.
/// </summary>
/// <remarks>
/// This guards the <em>host</em> (policy: which hosts may be reached) and the
/// scheme. The complementary <see cref="AddressGuard"/> guards the <em>resolved
/// IP</em> at connect time (link-local/metadata/loopback) — the two compose into
/// the layered SSRF defense the MCP Security Best Practices recommend.
/// </remarks>
public sealed class SsrfGuard
{
    private readonly HashSet<string> _allowedHosts;
    private readonly bool _allowPlainHttp;

    /// <summary>Creates a guard over the configured allow-list of hostnames.</summary>
    /// <param name="allowedHosts">The hosts the broker may reach (empty ⇒ none).</param>
    /// <param name="allowPlainHttp">
    /// When true, plain <c>http://</c> is permitted <em>to allow-listed hosts only</em>
    /// — the deliberate opt-in for reaching internal services that don't speak TLS
    /// (a homelab ClusterIP), aligned with the MCP Security BP "reject http except
    /// internal" carve-out. Default false: HTTPS only.
    /// </param>
    public SsrfGuard(IEnumerable<string> allowedHosts, bool allowPlainHttp = false)
    {
        _allowedHosts = new HashSet<string>(allowedHosts, StringComparer.OrdinalIgnoreCase);
        _allowPlainHttp = allowPlainHttp;
    }

    /// <summary>True when <paramref name="destination"/> is an absolute, scheme-allowed URL to an allow-listed host.</summary>
    public bool IsAllowed(string destination) =>
        Uri.TryCreate(destination, UriKind.Absolute, out var uri) && IsAllowed(uri);

    /// <summary>True when <paramref name="uri"/>'s scheme is allowed and its host is allow-listed.</summary>
    public bool IsAllowed(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        // Scheme: https always; http only when explicitly opted in (internal hosts).
        var schemeAllowed =
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || (_allowPlainHttp && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase));
        if (!schemeAllowed)
        {
            return false;
        }

        // Only allow-listed hosts. No raw IPs unless explicitly listed.
        return _allowedHosts.Contains(uri.Host);
    }
}
