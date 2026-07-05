using System.Buffers.Text;
using System.Security.Cryptography;
using Tessera.Core.OAuthMcp;

namespace Tessera.Providers.OAuthMcp;

/// <summary>The result of beginning an OAuth-MCP connect: where to send the browser, and the state.</summary>
/// <param name="AuthorizeUrl">The authorization-code redirect URL (carries the S256 challenge + state).</param>
/// <param name="State">The opaque anti-forgery state the callback must present back.</param>
public sealed record OAuthMcpConnectStart(Uri AuthorizeUrl, string State);

/// <summary>
/// Drives the per-user OAuth-MCP connect handshake (ADR 0027, spec W): the authorization-code
/// flow with PKCE (RFC 7636) and Resource Indicators (RFC 8707). It is the pure state machine —
/// it mints the PKCE pair + a high-entropy state, stashes the in-flight exchange, and builds the
/// authorize URL (<see cref="Begin"/>); then redeems the returned <c>code</c> for tokens via the
/// <see cref="OAuthMcpAcquirer"/> (<see cref="CompleteAsync"/>). Discovery (RFC 9728/8414) is the
/// caller's job — the resolved <see cref="OAuthMcpEndpoints"/> are handed in — so this class does
/// no I/O beyond the acquirer's back-channel token call and is fully unit-testable.
/// </summary>
/// <remarks>
/// The PKCE verifier lives only in the stashed <see cref="PendingAuthorization"/> (server-side) and
/// is sent ONLY on the back-channel token exchange — never on the authorize redirect. The
/// <c>state</c> is the single-use CSRF binding between the redirect and the callback; an unknown or
/// expired state is refused without ever calling the token endpoint. Acquisition is secretless
/// (tokens are written to the store by the acquirer, never returned here).
/// </remarks>
public sealed class OAuthMcpConnectService
{
    private readonly IPendingAuthorizationStore _pending;
    private readonly OAuthMcpAcquirer _acquirer;
    private readonly TimeSpan _ttl;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Creates the connect service over the pending-exchange store and the token acquirer.</summary>
    /// <param name="pending">The single-use, TTL'd store of in-flight authorizations.</param>
    /// <param name="acquirer">The token acquirer that redeems the code (and writes the bundle).</param>
    /// <param name="ttl">How long a begun authorization stays redeemable (default 10 minutes).</param>
    /// <param name="clock">Time source (tests inject a fake clock).</param>
    public OAuthMcpConnectService(
        IPendingAuthorizationStore pending,
        OAuthMcpAcquirer acquirer,
        TimeSpan? ttl = null,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(acquirer);
        _pending = pending;
        _acquirer = acquirer;
        _ttl = ttl ?? TimeSpan.FromMinutes(10);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Begin an authorization-code connect for <paramref name="principal"/> against a discovered
    /// OAuth-MCP: mint a fresh PKCE pair + state, stash the pending exchange, and return the
    /// authorize URL to redirect the user's browser to. Throws <see cref="ArgumentException"/> when
    /// the discovered authorization server lacks a usable authorize/token endpoint.
    /// </summary>
    public OAuthMcpConnectStart Begin(
        OAuthMcpEndpoints endpoints,
        string principal,
        string target,
        string secretName,
        Uri redirectUri,
        string clientId)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);
        ArgumentNullException.ThrowIfNull(redirectUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        if (endpoints.AuthorizationServer.AuthorizationEndpoint is not { } authStr
            || !Uri.TryCreate(authStr, UriKind.Absolute, out var authEndpoint))
        {
            throw new ArgumentException("authorization server metadata has no usable authorization_endpoint", nameof(endpoints));
        }

        if (endpoints.AuthorizationServer.TokenEndpoint is not { } tokenStr
            || !Uri.TryCreate(tokenStr, UriKind.Absolute, out var tokenEndpoint))
        {
            throw new ArgumentException("authorization server metadata has no usable token_endpoint", nameof(endpoints));
        }

        var pkce = PkcePair.Generate();
        var state = NewState();
        var authorizeUrl = OAuthAuthorizeUrl.Build(
            authEndpoint, clientId, redirectUri, endpoints.Scopes, endpoints.Resource, state, pkce);

        _pending.Put(state, new PendingAuthorization(
            Principal: principal,
            Target: target,
            SecretName: secretName,
            TokenEndpoint: tokenEndpoint,
            RedirectUri: redirectUri,
            ClientId: clientId,
            Resource: endpoints.Resource,
            Verifier: pkce.Verifier,
            ExpiresAt: _clock() + _ttl));

        return new OAuthMcpConnectStart(authorizeUrl, state);
    }

    /// <summary>
    /// Complete the connect: look up the single-use <paramref name="state"/> and, if it is live,
    /// redeem <paramref name="code"/> for tokens at the stashed token endpoint (with the stashed
    /// PKCE verifier, redirect URI, client id and resource) and write the per-principal bundle. An
    /// unknown or expired state is refused WITHOUT calling the token endpoint.
    /// </summary>
    public Task<OAuthAcquireResult> CompleteAsync(string state, string code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return Task.FromResult(OAuthAcquireResult.Failed("callback carried no authorization code"));
        }

        var pending = _pending.Take(state, _clock());
        if (pending is null)
        {
            return Task.FromResult(OAuthAcquireResult.Failed("unknown or expired authorization state"));
        }

        return _acquirer.AcquireAsync(
            pending.TokenEndpoint,
            pending.ClientId,
            pending.RedirectUri,
            code,
            pending.Verifier,
            pending.Resource,
            pending.SecretName,
            cancellationToken);
    }

    private static string NewState()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url.EncodeToString(bytes);
    }
}
