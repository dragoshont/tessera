namespace Tessera.Core.OAuthMcp;

/// <summary>
/// The ADR-0027 §4 audience guard. An OAuth-MCP recipe's injected token is minted for a
/// specific RFC 9728 <c>resource</c> (the recipe's <c>McpUrl</c>). Because the proxy
/// egress lets the caller drive the upstream URL (validated only against the global SSRF
/// allow-list), egress MUST additionally refuse any upstream <em>outside</em> that
/// resource — otherwise a hostile or prompt-injected caller could steer the mobbin token
/// at a different allow-listed host (a token confused-deputy). This complements, and does
/// not replace, the SSRF host allow-list.
/// </summary>
public static class OAuthMcpAudience
{
    /// <summary>
    /// True when <paramref name="upstream"/> is within the OAuth-MCP resource named by
    /// <paramref name="mcpUrl"/>: identical scheme + authority (host and port), and a path
    /// at or under the MCP endpoint's path. A malformed <paramref name="mcpUrl"/> binds to
    /// nothing (fail-closed → refuse).
    /// </summary>
    public static bool IsBound(Uri upstream, string mcpUrl)
    {
        ArgumentNullException.ThrowIfNull(upstream);

        if (!Uri.TryCreate(mcpUrl, UriKind.Absolute, out var resource))
        {
            return false;
        }

        if (!string.Equals(upstream.Scheme, resource.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(upstream.Authority, resource.Authority, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var resourcePath = resource.AbsolutePath.TrimEnd('/');
        var upstreamPath = upstream.AbsolutePath.TrimEnd('/');
        return upstreamPath.Equals(resourcePath, StringComparison.Ordinal)
            || upstreamPath.StartsWith(resourcePath + "/", StringComparison.Ordinal);
    }
}
