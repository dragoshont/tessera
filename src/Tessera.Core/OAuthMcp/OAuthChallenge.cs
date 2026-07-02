using System.Text.RegularExpressions;

namespace Tessera.Core.OAuthMcp;

/// <summary>
/// A parsed <c>WWW-Authenticate: Bearer …</c> challenge (RFC 7235 / RFC 6750). We
/// care about the RFC 9728 <c>resource_metadata</c> parameter, which points a client
/// at the protected-resource metadata document that names the resource's
/// authorization server(s).
/// </summary>
/// <param name="Scheme">The auth scheme (always <c>Bearer</c> for a parsed value).</param>
/// <param name="Parameters">The auth-params, case-insensitive by key.</param>
public sealed record OAuthChallenge(string Scheme, IReadOnlyDictionary<string, string> Parameters)
{
    /// <summary>The RFC 9728 protected-resource metadata URL, if present.</summary>
    public string? ResourceMetadata =>
        Parameters.TryGetValue("resource_metadata", out var v) ? v : null;

    private static readonly Regex ParamRx = new(
        "(?<k>[A-Za-z0-9_-]+)\\s*=\\s*(?:\"(?<v>[^\"]*)\"|(?<v>[^,\\s]+))",
        RegexOptions.Compiled);

    /// <summary>
    /// Parse a <c>WWW-Authenticate</c> header value. Returns null when the value is
    /// empty or is not a <c>Bearer</c> challenge. Tolerant of extra auth-params.
    /// </summary>
    public static OAuthChallenge? Parse(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return null;
        }

        var trimmed = headerValue.TrimStart();
        var space = trimmed.IndexOf(' ');
        var scheme = space < 0 ? trimmed : trimmed[..space];
        if (!scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = space < 0 ? string.Empty : trimmed[(space + 1)..];
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in ParamRx.Matches(rest))
        {
            parameters[m.Groups["k"].Value] = m.Groups["v"].Value;
        }

        return new OAuthChallenge(scheme, parameters);
    }
}
