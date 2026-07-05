using System.Net.Http;
using Tessera.Core.Egress;

namespace Tessera.Core.OAuthMcp;

/// <summary>
/// The resolved OAuth endpoints for an upstream OAuth-MCP: the RFC 9728 resource and
/// its RFC 8414 authorization-server metadata (authorize + token endpoints). This is
/// what the acquisition driver (ADR 0027 §P3) needs to mint a per-user token.
/// </summary>
/// <param name="Resource">The RFC 9728 <c>resource</c> the acquired token is bound to.</param>
/// <param name="AuthorizationServer">The RFC 8414 metadata (authorize + token).</param>
/// <param name="Scopes">The scopes to request (from the recipe or the resource doc).</param>
public sealed record OAuthMcpEndpoints(
    string Resource,
    AuthorizationServerMetadata AuthorizationServer,
    IReadOnlyList<string> Scopes);

/// <summary>
/// RFC 9728 + RFC 8414 discovery for an upstream OAuth-MCP (ADR 0027). Probes the MCP
/// endpoint, reads <c>resource_metadata</c> from its 401 challenge, fetches the
/// protected-resource document, then the authorization-server metadata. Pure over an
/// injected <see cref="HttpClient"/> so it is fully testable with a stub handler.
/// </summary>
/// <remarks>
/// SECURITY (HL-4 / ADR 0027 §5): the <c>resource_metadata</c> URL and the
/// <c>authorization_servers</c> entries are UNTRUSTED — they come from the upstream's
/// own 401 challenge and document. The caller MUST construct this with an SSRF-guarded
/// <see cref="HttpClient"/> (a public-only connect guard + host allow-list, the same
/// posture as <c>InjectionEgress</c>, ADR 0014) so a hostile upstream cannot steer
/// discovery at an internal address.
/// </remarks>
public sealed class OAuthMcpDiscovery(HttpClient http, SsrfGuard guard)
{
    private const string InitializeBody =
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}";

    private readonly HttpClient _http = http;
    private readonly SsrfGuard _guard = guard;

    /// <summary>Probe the MCP endpoint (unauthenticated) and classify it (RFC 9728).</summary>
    public async Task<OAuthMcpProbe> ProbeAsync(string mcpUrl, CancellationToken ct = default)
    {
        if (!IsAllowed(mcpUrl))
        {
            return new OAuthMcpProbe(false, null);   // off the SSRF allow-list ⇒ not usable
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, mcpUrl)
        {
            Content = new StringContent(InitializeBody),
        };
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        req.Content.Headers.ContentType = new("application/json");

        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var wwwAuth = res.Headers.WwwAuthenticate.Count > 0
            ? string.Join(", ", res.Headers.WwwAuthenticate)
            : null;
        return OAuthMcpClassifier.Classify((int)res.StatusCode, wwwAuth);
    }

    /// <summary>
    /// Given an RFC 9728 <c>resource_metadata</c> URL, fetch the protected-resource
    /// document and its authorization-server metadata. Returns null when the target is
    /// not a usable OAuth-MCP (no authorization server, or no token endpoint).
    /// <paramref name="preferredScopes"/> (from the recipe) win over the resource's
    /// advertised scopes when present.
    /// </summary>
    public async Task<OAuthMcpEndpoints?> DiscoverAsync(
        string resourceMetadataUrl,
        IReadOnlyList<string>? preferredScopes = null,
        CancellationToken ct = default)
    {
        // The resource_metadata URL comes from the upstream's 401 challenge — UNTRUSTED.
        if (!IsAllowed(resourceMetadataUrl))
        {
            return null;
        }

        var prJson = await _http.GetStringAsync(resourceMetadataUrl, ct).ConfigureAwait(false);
        var pr = ProtectedResourceMetadata.FromJson(prJson);
        var servers = pr?.AuthorizationServers;
        var issuer = servers is { Count: > 0 } ? servers[0] : null;
        if (pr?.Resource is null || string.IsNullOrWhiteSpace(issuer))
        {
            return null;
        }

        var asUrl = issuer.TrimEnd('/') + "/.well-known/oauth-authorization-server";
        if (!IsAllowed(asUrl))   // the authorization server comes from the (untrusted) resource doc
        {
            return null;
        }

        var asJson = await _http.GetStringAsync(asUrl, ct).ConfigureAwait(false);
        var asMeta = AuthorizationServerMetadata.FromJson(asJson);
        if (asMeta?.TokenEndpoint is null)
        {
            return null;
        }

        // The endpoints Tessera will ACT ON must be allow-listed too: the token endpoint
        // (back-channel exchange) and — critically — the authorization endpoint, where the
        // user's BROWSER is redirected. A hostile upstream must not steer that redirect off the
        // allow-list (host + scheme enforced by the guard).
        if (asMeta.AuthorizationEndpoint is null
            || !IsAllowed(asMeta.AuthorizationEndpoint)
            || !IsAllowed(asMeta.TokenEndpoint))
        {
            return null;
        }

        var scopes = preferredScopes is { Count: > 0 }
            ? preferredScopes
            : pr.ScopesSupported ?? [];
        return new OAuthMcpEndpoints(pr.Resource, asMeta, scopes);
    }

    /// <summary>True when <paramref name="url"/> is an absolute URL whose host + scheme pass the
    /// SSRF allow-list — applied to every untrusted discovery URL and the endpoints we act on.</summary>
    private bool IsAllowed(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && _guard.IsAllowed(uri);
}
