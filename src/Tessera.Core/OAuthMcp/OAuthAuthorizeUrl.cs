namespace Tessera.Core.OAuthMcp;

/// <summary>
/// Builds the authorization-code redirect URL (OAuth 2.1 authorization request +
/// PKCE RFC 7636 + Resource Indicators RFC 8707). Pure: given the discovered
/// authorization endpoint and the request parameters it returns the exact URL the
/// user's browser is sent to — no I/O, fully testable.
/// </summary>
/// <remarks>
/// The <c>resource</c> parameter (RFC 8707) audience-binds the issued token to the MCP
/// resource — the SAME value the egress audience guard enforces on the way out
/// (ADR 0027 §4, <see cref="OAuthMcpAudience"/>). Requesting the token for the resource
/// and refusing to inject it anywhere else closes the confused-deputy gap end to end.
/// The verifier is never placed on this URL; only its S256 <c>code_challenge</c> is.
/// </remarks>
public static class OAuthAuthorizeUrl
{
    /// <summary>
    /// Build the authorize URL. All values are URL-encoded; <paramref name="scopes"/> are
    /// space-joined per OAuth. <paramref name="state"/> is the caller's CSRF/anti-forgery
    /// nonce (echoed back on the redirect and verified there — this builder does not mint it).
    /// </summary>
    public static Uri Build(
        Uri authorizationEndpoint,
        string clientId,
        Uri redirectUri,
        IReadOnlyList<string> scopes,
        string resource,
        string state,
        PkcePair pkce)
    {
        ArgumentNullException.ThrowIfNull(authorizationEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(redirectUri);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        ArgumentNullException.ThrowIfNull(pkce);

        var parameters = new (string Key, string Value)[]
        {
            ("response_type", "code"),
            ("client_id", clientId),
            ("redirect_uri", redirectUri.ToString()),
            ("scope", string.Join(' ', scopes)),
            ("state", state),
            ("code_challenge", pkce.Challenge),
            ("code_challenge_method", PkcePair.Method),
            ("resource", resource),
        };

        var parts = new List<string>(parameters.Length);
        foreach (var (key, value) in parameters)
        {
            parts.Add($"{key}={Uri.EscapeDataString(value)}");
        }

        var query = string.Join('&', parts);
        var builder = new UriBuilder(authorizationEndpoint);
        var existing = builder.Query.TrimStart('?');
        builder.Query = existing.Length > 0 ? $"{existing}&{query}" : query;
        return builder.Uri;
    }
}
