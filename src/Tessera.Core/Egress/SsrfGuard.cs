namespace Tessera.Core.Egress;

using System.Text.RegularExpressions;

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
/// <para>
/// Most entries are exact host names. An entry prefixed <c>re:</c> is an anchored
/// host <em>pattern</em> — for a provider whose endpoint is a runtime-discovered
/// partition host (iCloud's <c>pNN-caldav.icloud.com</c> from RFC 6764 discovery),
/// where listing every partition is brittle. A pattern must match the <b>whole</b>
/// host (the match is span-checked, so a missing anchor cannot let
/// <c>icloud.com.evil.net</c> through), is case-insensitive, and is evaluated under
/// a short timeout that fails <em>closed</em> (a pathological pattern denies rather
/// than hangs). Keep patterns tight (e.g. <c>^p\d{1,3}-caldav\.icloud\.com$</c>) —
/// never a bare <c>.*\.icloud\.com</c>, which is far broader than the DAV surface.
/// </para>
/// </remarks>
public sealed class SsrfGuard
{
    private readonly HashSet<string> _allowedHosts;
    private readonly IReadOnlyList<Regex> _hostPatterns;
    private readonly bool _allowPlainHttp;

    /// <summary>Creates a guard over the configured allow-list of hostnames.</summary>
    /// <param name="allowedHosts">The hosts the broker may reach (empty ⇒ none). An entry prefixed <c>re:</c> is an anchored host pattern.</param>
    /// <param name="allowPlainHttp">
    /// When true, plain <c>http://</c> is permitted <em>to allow-listed hosts only</em>
    /// — the deliberate opt-in for reaching internal services that don't speak TLS
    /// (a homelab ClusterIP), aligned with the MCP Security BP "reject http except
    /// internal" carve-out. Default false: HTTPS only.
    /// </param>
    public SsrfGuard(IEnumerable<string> allowedHosts, bool allowPlainHttp = false)
    {
        var exact = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var patterns = new List<Regex>();
        foreach (var entry in allowedHosts)
        {
            if (entry.StartsWith("re:", StringComparison.Ordinal))
            {
                var pattern = entry["re:".Length..];
                if (pattern.Length > 0)
                {
                    // CultureInvariant + a 100ms timeout: a catastrophic-backtracking
                    // pattern denies (RegexMatchTimeoutException → fail closed) instead
                    // of hanging the egress (OWASP ReDoS).
                    patterns.Add(new Regex(
                        pattern,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(100)));
                }
            }
            else
            {
                exact.Add(entry);
            }
        }

        _allowedHosts = exact;
        _hostPatterns = patterns;
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
        return _allowedHosts.Contains(uri.Host) || MatchesPattern(uri.Host);
    }

    /// <summary>
    /// True when <paramref name="host"/> matches an anchored host pattern in full.
    /// The match must span the whole host (no partial match) and is evaluated under
    /// the pattern's timeout; a timeout denies (fail closed).
    /// </summary>
    private bool MatchesPattern(string host)
    {
        foreach (var pattern in _hostPatterns)
        {
            Match match;
            try
            {
                match = pattern.Match(host);
            }
            catch (RegexMatchTimeoutException)
            {
                continue; // pathological pattern → this entry denies (fail closed)
            }

            if (match.Success && match.Index == 0 && match.Length == host.Length)
            {
                return true;
            }
        }

        return false;
    }
}
