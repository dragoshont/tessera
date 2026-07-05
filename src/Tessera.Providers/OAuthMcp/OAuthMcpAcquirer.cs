using System.Text.Json;
using Tessera.Core.Egress;
using Tessera.Core.Stores;

namespace Tessera.Providers.OAuthMcp;

/// <summary>The outcome of an OAuth token acquisition or refresh (secret-free).</summary>
public enum OAuthAcquireStatus
{
    /// <summary>Tokens were acquired/rotated and written to the store per-principal.</summary>
    Acquired = 0,

    /// <summary>The grant is dead — an interactive re-authorization is required (report, never auto-login).</summary>
    Dead,

    /// <summary>The token endpoint host is off the SSRF allow-list, or the transport/store failed.</summary>
    Error,
}

/// <summary>The result of an acquisition/refresh (secret-free: a status + a non-secret detail).</summary>
/// <param name="Status">What happened.</param>
/// <param name="Detail">A secret-free explanation (never token bytes).</param>
public sealed record OAuthAcquireResult(OAuthAcquireStatus Status, string Detail = "")
{
    internal static OAuthAcquireResult Ok(string detail = "tokens written") => new(OAuthAcquireStatus.Acquired, detail);
    internal static OAuthAcquireResult Failed(string detail) => new(OAuthAcquireStatus.Error, detail);
    internal static OAuthAcquireResult DeadGrant(string detail) => new(OAuthAcquireStatus.Dead, detail);
}

/// <summary>
/// Acquires and refreshes per-principal OAuth tokens for an OAuth-MCP upstream (ADR 0027,
/// spec P3) against its RFC 8414 token endpoint. Both legs are the same back-channel
/// form-POST — <c>authorization_code</c> (initial, carrying the PKCE verifier) and
/// <c>refresh_token</c> (rotation) — so this one class owns the whole OAuth 2.1 token
/// exchange.
/// </summary>
/// <remarks>
/// This is deliberately <em>not</em> the <see cref="SessionRefresher"/> path.
/// <see cref="SessionRefresher"/> injects a stored session credential into the request
/// <em>headers</em> and lets the server re-issue (the RM/proprietary keep-warm model),
/// whereas OAuth carries the grant in a <c>application/x-www-form-urlencoded</c> body with a
/// <c>grant_type</c>. They share the store, the <see cref="SsrfGuard"/>, and the token-response
/// parse — not the request shape.
/// <para><b>Secretless.</b> Every issued/rotated bundle is written through
/// <see cref="ICredentialWriter"/>; this class NEVER returns token bytes to a caller
/// (ADR 0014) — a caller learns only a status.</para>
/// <para><b>Single-writer (ADR 0026).</b> A refresh token is single-use: the AS MAY rotate it
/// on every refresh. This class must be driven by the ONE rotation owner (the
/// <c>SessionRefreshOrchestrator</c> pass), never a throwaway process — two concurrent
/// refreshers would double-spend the single-use token and kill the chain (the RM outage
/// lesson). It preserves the current refresh token when the AS returns none (RFC 6749 §6).</para>
/// <para><b>SSRF.</b> The token endpoint is checked against the same <see cref="SsrfGuard"/>
/// allow-list the data egress uses before any request — the acquirer can never reach a host
/// the egress could not. There is no unguarded constructor.</para>
/// </remarks>
public sealed class OAuthMcpAcquirer
{
    /// <summary>Bundle <c>Extra</c> key for the discovered token endpoint — persisted at acquire
    /// time so a later refresh needs no re-discovery (and the rotation owner needs no HTTP client).</summary>
    public const string ExtraTokenEndpoint = "oauth_token_endpoint";

    /// <summary>Bundle <c>Extra</c> key for the OAuth client id used on the token exchange.</summary>
    public const string ExtraClientId = "oauth_client_id";

    /// <summary>Bundle <c>Extra</c> key for the RFC 8707 resource the token is bound to.</summary>
    public const string ExtraResource = "oauth_resource";

    private readonly IHttpTransport _transport;
    private readonly ICredentialWriter _writer;
    private readonly SsrfGuard _guard;

    /// <summary>Creates an acquirer over the SSRF-guarded transport and the store writer.</summary>
    /// <param name="transport">The HTTP transport that performs the token-endpoint call.</param>
    /// <param name="writer">The store writer for the issued/rotated bundle.</param>
    /// <param name="guard">The SSRF allow-list the token endpoint must pass (required — the acquirer egresses).</param>
    public OAuthMcpAcquirer(IHttpTransport transport, ICredentialWriter writer, SsrfGuard guard)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(guard);
        _transport = transport;
        _writer = writer;
        _guard = guard;
    }

    /// <summary>
    /// Exchange an authorization <paramref name="code"/> (with its PKCE <paramref name="verifier"/>)
    /// for tokens at <paramref name="tokenEndpoint"/>, then write the bundle to
    /// <paramref name="secretName"/> (the per-principal credential). The RFC 8707
    /// <paramref name="resource"/> audience-binds the token to the MCP.
    /// </summary>
    public Task<OAuthAcquireResult> AcquireAsync(
        Uri tokenEndpoint,
        string clientId,
        Uri redirectUri,
        string code,
        string verifier,
        string resource,
        string secretName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokenEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(redirectUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(verifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);

        var form = new (string Key, string Value)[]
        {
            ("grant_type", "authorization_code"),
            ("code", code),
            ("redirect_uri", redirectUri.ToString()),
            ("client_id", clientId),
            ("code_verifier", verifier),
            ("resource", resource),
        };
        // Seed the bundle with the (non-secret) refresh context so a later rotation is
        // self-contained — the rotation owner refreshes from the stored bundle alone,
        // without re-running discovery or holding an HTTP client (see RefreshStoredAsync).
        var context = new CredentialBundle(Extra: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ExtraTokenEndpoint] = tokenEndpoint.ToString(),
            [ExtraClientId] = clientId,
            [ExtraResource] = resource,
        });
        return ExchangeAsync(tokenEndpoint, form, context, secretName, cancellationToken);
    }

    /// <summary>
    /// Rotate tokens with the <paramref name="current"/> bundle's refresh token
    /// (<c>grant_type=refresh_token</c>) at <paramref name="tokenEndpoint"/> and write the
    /// rotated bundle back to <paramref name="secretName"/>. When the AS returns no new
    /// refresh token the current one is preserved (RFC 6749 §6). A <c>400</c>/<c>401</c> with
    /// an <c>invalid_grant</c> body ⇒ the refresh token is dead: reported, never auto-logged-in.
    /// </summary>
    public Task<OAuthAcquireResult> RefreshAsync(
        Uri tokenEndpoint,
        string clientId,
        string resource,
        string secretName,
        CredentialBundle current,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokenEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);
        ArgumentNullException.ThrowIfNull(current);

        if (!current.HasRefreshToken)
        {
            return Task.FromResult(OAuthAcquireResult.DeadGrant(
                "no refresh token to rotate — interactive re-authorization needed"));
        }

        var form = new (string Key, string Value)[]
        {
            ("grant_type", "refresh_token"),
            ("refresh_token", current.RefreshToken!),
            ("client_id", clientId),
            ("resource", resource),
        };
        return ExchangeAsync(tokenEndpoint, form, current, secretName, cancellationToken);
    }

    /// <summary>
    /// Rotate a stored per-principal bundle using ONLY the OAuth refresh context it carries
    /// in <see cref="CredentialBundle.Extra"/> (the token endpoint, client id and resource
    /// stamped by <see cref="AcquireAsync"/>) plus its refresh token — no re-discovery. This is
    /// the entry point the rotation owner (<c>SessionRefreshOrchestrator</c>) calls for an
    /// <c>oauth-mcp</c> binding. A bundle without the stamped context is reported as an error
    /// (it was not acquired through the OAuth path), never silently skipped.
    /// </summary>
    public Task<OAuthAcquireResult> RefreshStoredAsync(
        string secretName,
        CredentialBundle current,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);
        ArgumentNullException.ThrowIfNull(current);

        var extra = current.Extra;
        if (extra is null
            || !extra.TryGetValue(ExtraTokenEndpoint, out var endpoint)
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var tokenEndpoint)
            || !extra.TryGetValue(ExtraClientId, out var clientId) || string.IsNullOrWhiteSpace(clientId)
            || !extra.TryGetValue(ExtraResource, out var resource) || string.IsNullOrWhiteSpace(resource))
        {
            return Task.FromResult(OAuthAcquireResult.Failed(
                "bundle is missing the OAuth refresh context (token endpoint/client id/resource)"));
        }

        return RefreshAsync(tokenEndpoint, clientId, resource, secretName, current, cancellationToken);
    }

    private async Task<OAuthAcquireResult> ExchangeAsync(
        Uri tokenEndpoint,
        (string Key, string Value)[] form,
        CredentialBundle current,
        string secretName,
        CancellationToken cancellationToken)
    {
        // SSRF: the token endpoint must be on the same host allow-list as the data egress,
        // checked BEFORE any request leaves the process.
        if (!_guard.IsAllowed(tokenEndpoint))
        {
            return OAuthAcquireResult.Failed("token endpoint host is not on the SSRF allow-list");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/x-www-form-urlencoded",
            ["Accept"] = "application/json",
        };

        TransportResponse response;
        try
        {
            response = await _transport
                .SendAsync("POST", tokenEndpoint.ToString(), headers, EncodeForm(form), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return OAuthAcquireResult.Failed(ex.Message);
        }

        // A rejected grant (RFC 6749 §5.2 invalid_grant) means the code/refresh token is
        // spent or revoked — a human must re-authorize; we never drive a login.
        if (response.Status is 400 or 401 && IsInvalidGrant(response.Body))
        {
            return OAuthAcquireResult.DeadGrant(
                $"token endpoint rejected the grant (HTTP {response.Status}) — interactive re-authorization needed");
        }

        if (response.Status is < 200 or >= 300)
        {
            return OAuthAcquireResult.Failed($"token endpoint HTTP {response.Status}");
        }

        var (access, refresh) = ParseTokens(response.Body);
        if (string.IsNullOrEmpty(access))
        {
            return OAuthAcquireResult.Failed("token response carried no access_token");
        }

        // Preserve the current refresh token when the AS did not rotate it (RFC 6749 §6):
        // a non-rotating AS omits refresh_token on the refresh response.
        var bundle = current with
        {
            AccessToken = access,
            RefreshToken = string.IsNullOrEmpty(refresh) ? current.RefreshToken : refresh,
        };

        try
        {
            await _writer.PutBundleAsync(secretName, bundle, cancellationToken).ConfigureAwait(false);
        }
        catch (StoreException ex)
        {
            return OAuthAcquireResult.Failed($"write failed: {ex.Message}");
        }

        return OAuthAcquireResult.Ok();
    }

    private static string EncodeForm((string Key, string Value)[] form)
    {
        var parts = new List<string>(form.Length);
        foreach (var (key, value) in form)
        {
            parts.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
        }
        return string.Join('&', parts);
    }

    private static (string? Access, string? Refresh) ParseTokens(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return (null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var access = doc.RootElement.TryGetProperty("access_token", out var a) && a.ValueKind == JsonValueKind.String
                ? a.GetString()
                : null;
            var refresh = doc.RootElement.TryGetProperty("refresh_token", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : null;
            return (access, refresh);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static bool IsInvalidGrant(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var e)
                && e.ValueKind == JsonValueKind.String
                && string.Equals(e.GetString(), "invalid_grant", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
