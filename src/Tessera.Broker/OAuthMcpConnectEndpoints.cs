using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Tessera.Core.Configuration;
using Tessera.Core.OAuthMcp;
using Tessera.Core.Portal;
using Tessera.Core.Recipes;
using Tessera.Identity;
using Tessera.Providers.OAuthMcp;

namespace Tessera.Broker;

/// <summary>
/// The per-user OAuth-MCP connect surface (ADR 0027, spec W). Two endpoints, mapped only when
/// <c>oauthMcp.enabled</c>:
/// <list type="bullet">
/// <item><c>POST /oauth/mcp/connect</c> — an operator BEGINS a connect: discover the upstream AS
/// (RFC 9728/8414), mint a PKCE pair + single-use anti-forgery state, and hand back the authorize
/// URL to send the browser to. Connecting on ANOTHER person's behalf is operator-only.</item>
/// <item><c>GET /oauth/mcp/callback</c> — the AS redirect lands here (PUBLIC; the user's browser
/// hits it after consent). The single-use <c>state</c> is the CSRF proof binding the callback to a
/// legitimate begin; an unknown/expired state is refused WITHOUT a token call. On a real
/// acquisition the per-principal binding is created — the credential itself is written by the
/// acquirer, never surfaced here (secretless).</item>
/// </list>
/// </summary>
public static class OAuthMcpConnectEndpoints
{
    /// <summary>Maps the begin + callback endpoints. Call only when <c>oauthMcp.enabled</c>.</summary>
    public static void MapOAuthMcpConnect(this WebApplication app)
    {
        app.MapPost("/oauth/mcp/connect", async (
            HttpContext ctx,
            OAuthMcpConnectRequest body,
            ITokenValidator validator,
            PortalService portal,
            TesseraConfig config,
            OAuthMcpConnectService connect,
            OAuthMcpDiscovery discovery,
            IReadOnlyList<Recipe> recipes,
            CancellationToken ct) =>
        {
            var user = await PortalEndpoints.ResolveEndUserAsync(ctx, validator, config).ConfigureAwait(false);
            var callerDisplay = user?.PreferredUsername ?? user?.Subject;
            var caller = user?.CanonicalPrincipalId;
            if (caller is null || callerDisplay is null)
            {
                return Results.Json(new { error = "unauthenticated" }, statusCode: 401);
            }

            if (body is null || string.IsNullOrWhiteSpace(body.Target))
            {
                return Results.Json(new { error = "bad request: target is required" }, statusCode: 400);
            }

            var target = body.Target.Trim();
            if (!string.IsNullOrWhiteSpace(body.Principal)
                && !string.Equals(body.Principal.Trim(), callerDisplay, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(body.Principal.Trim(), caller, StringComparison.Ordinal))
            {
                return Results.Json(
                    new { error = "forbidden: the connection owner must authorize their own account" },
                    statusCode: 403);
            }

            var principal = caller;

            var recipe = recipes.FirstOrDefault(r => string.Equals(r.Target, target, StringComparison.Ordinal));
            if (recipe?.OAuthMcp is not { } oauth)
            {
                return Results.Json(new { error = $"'{target}' is not an oauth-mcp target" }, statusCode: 400);
            }

            var secretName = MintSecretName(target, principal);
            OAuthMcpConnectStart? start;
            try
            {
                start = await connect.BeginForRecipeAsync(
                    discovery, oauth.McpUrl, oauth.Scopes ?? [], principal, target, secretName,
                    new Uri(config.OAuthMcp.RedirectUri), config.OAuthMcp.ClientId, ct).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                // The discovered AS metadata had no usable authorize/token endpoint.
                return Results.Json(new { error = ex.Message }, statusCode: 502);
            }

            if (start is null)
            {
                return Results.Json(
                    new { error = $"'{target}' did not answer as a reachable OAuth-MCP" },
                    statusCode: 502);
            }

            return Results.Json(new { authorizeUrl = start.AuthorizeUrl.ToString(), state = start.State });
        });

        app.MapGet("/oauth/mcp/callback", async (
            PortalService portal,
            OAuthMcpConnectService connect,
            string? code,
            string? state,
            string? error,
            CancellationToken ct) =>
        {
            if (!string.IsNullOrEmpty(error))
            {
                return Results.Text(
                    $"Connection failed at the provider: {SanitizeForText(error)}",
                    "text/plain; charset=utf-8", statusCode: 400);
            }

            if (string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(code))
            {
                return Results.Text(
                    "Connection failed: the callback was missing its code or state.",
                    "text/plain; charset=utf-8", statusCode: 400);
            }

            var result = await connect.CompleteAsync(state, code, ct).ConfigureAwait(false);
            if (result.Status != OAuthAcquireStatus.Acquired
                || result.Target is null || result.Principal is null || result.SecretName is null)
            {
                return Results.Text(
                    $"Connection failed: {SanitizeForText(result.Detail)}",
                    "text/plain; charset=utf-8", statusCode: 400);
            }

            // Create the per-principal binding now that a real token was written (the state proved
            // this callback corresponds to the operator's authenticated begin).
                await portal.AddConnectionAsync(
                    result.Target,
                    result.Principal,
                    result.SecretName,
                    principalId: result.Principal,
                    cancellationToken: ct)
                .ConfigureAwait(false);

            return Results.Text(
                "Connected. You can close this window and return to Tessera.",
                "text/plain; charset=utf-8");
        });
    }

    /// <summary>
    /// A deterministic, KeyVault-safe (<c>^[0-9a-zA-Z-]+$</c>), collision-resistant store key for a
    /// (target, principal): a readable slug plus a SHA-256 discriminator, so two principals that
    /// slug to the same string never collide and re-connecting the same pair overwrites the same
    /// secret (no orphan).
    /// </summary>
    private static string MintSecretName(string target, string principal)
    {
        var discriminator = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{target}\u0000{principal}")))[..16].ToLowerInvariant();
        return $"mcp-{Slug(target)}-{discriminator}";
    }

    private static string Slug(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            sb.Append(char.IsAsciiLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-');
        }

        var slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "target" : slug[..Math.Min(slug.Length, 40)];
    }

    private static string SanitizeForText(string value)
    {
        // The callback body is text/plain (no HTML sink); keep a reflected provider error tidy and
        // free of control characters, and bounded.
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            sb.Append(char.IsControl(ch) ? ' ' : ch);
        }

        var text = sb.ToString();
        return text[..Math.Min(text.Length, 200)];
    }
}

/// <summary>
/// The begin-connect request body: the oauth-mcp target, and optionally the person it is for (an
/// operator may connect on another's behalf; default = the authenticated caller).
/// </summary>
/// <param name="Target">The oauth-mcp recipe target to connect.</param>
/// <param name="Principal">The person the credential is for (default = the authenticated caller).</param>
public sealed record OAuthMcpConnectRequest(string Target, string? Principal);
