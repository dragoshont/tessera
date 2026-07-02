namespace Tessera.Core.OAuthMcp;

/// <summary>The verdict of probing a candidate MCP endpoint.</summary>
/// <param name="IsOAuthMcp">True when the target answered like an OAuth-MCP: HTTP 401
/// plus a <c>Bearer</c> challenge carrying <c>resource_metadata</c> (RFC 9728).</param>
/// <param name="ResourceMetadataUrl">The RFC 9728 metadata URL to discover the
/// authorization server from (null when not an OAuth-MCP).</param>
public sealed record OAuthMcpProbe(bool IsOAuthMcp, string? ResourceMetadataUrl);

/// <summary>
/// Decides whether a target "ships its own OAuth-MCP" from a single unauthenticated
/// probe — the classifier at the heart of ADR 0027. An OAuth-MCP answers <c>401</c>
/// with <c>WWW-Authenticate: Bearer resource_metadata="…"</c> (RFC 9728); anything else
/// is a class-2 harvest-and-inject target and must not be treated as an OAuth-MCP
/// (fail-safe: unknown ⇒ not-OAuth-MCP).
/// </summary>
public static class OAuthMcpClassifier
{
    /// <summary>Classify a probe response by status code + <c>WWW-Authenticate</c>.</summary>
    public static OAuthMcpProbe Classify(int statusCode, string? wwwAuthenticate)
    {
        if (statusCode != 401)
        {
            return new OAuthMcpProbe(false, null);
        }

        var url = OAuthChallenge.Parse(wwwAuthenticate)?.ResourceMetadata;
        return new OAuthMcpProbe(!string.IsNullOrWhiteSpace(url), url);
    }
}
