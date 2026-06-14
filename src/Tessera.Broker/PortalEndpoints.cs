using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Tessera.Core.Configuration;
using Tessera.Core.Portal;
using Tessera.Identity;

namespace Tessera.Broker;

/// <summary>
/// The admin-portal HTTP surface (ADR 0016): a thin, read-mostly projection over
/// the broker's existing model. It never returns a secret value — only people,
/// connection health (presence flags + status), and a fail-closed live-view handle.
/// Authorization mirrors the broker: the caller's principal comes from a verified
/// forwarded OIDC token (or, on loopback in dev mode, an explicit dev header), and
/// the admin surface is gated by the <c>portal.admins</c> allow-list.
/// </summary>
internal static class PortalEndpoints
{
    private const string DevPrincipalHeader = "X-Tessera-Dev-Principal";

    public static void MapPortalEndpoints(this WebApplication app)
    {
        // Runtime auth config for the SPA: how to sign in (dev vs OIDC) without
        // baking it into the build. The SPA fetches this first, then either shows
        // the loopback dev login (pick a principal) or starts the Entra redirect.
        app.MapGet("/portal/config", (TesseraConfig config) =>
        {
            var devLoopback = config.Identity.Mode == "dev" && config.Server.IsLoopback;
            var oidc = config.Identity.Mode == "oidc" && !string.IsNullOrWhiteSpace(config.Identity.Oidc.Issuer)
                ? new OidcConfigDto(
                    config.Identity.Oidc.Issuer,
                    config.Identity.Oidc.Audience,
                    // The SPA signs in as a public client; the scope it must request
                    // is the broker's audience so the forwarded access token's `aud`
                    // matches what the broker validates (the delegation crux). The
                    // explicit oidc.spaScope wins; else derive a sensible default.
                    !string.IsNullOrWhiteSpace(config.Identity.Oidc.SpaScope)
                        ? config.Identity.Oidc.SpaScope
                        : string.IsNullOrWhiteSpace(config.Identity.Oidc.Audience)
                            ? "openid profile email"
                            : $"openid profile email {config.Identity.Oidc.Audience}/.default")
                : null;
            return Results.Json(new PortalConfigDto(
                AuthMode: devLoopback ? "dev" : (oidc is not null ? "oidc" : "none"),
                DevLoopback: devLoopback,
                Oidc: oidc));
        });

        // Who am I, and am I an operator? Drives the sidebar (the ADMIN section is
        // shown only to admins) and the "(you)" tag.
        app.MapGet("/portal/me", async (HttpContext ctx, ITokenValidator validator, PortalService portal, TesseraConfig config) =>
        {
            var principal = await ResolvePrincipalAsync(ctx, validator, config).ConfigureAwait(false);
            if (principal is null)
            {
                return Results.Json(new { error = "unauthenticated: no verified principal (forward an OIDC token)" }, statusCode: 401);
            }

            var isAdmin = portal.IsAdmin(principal);
            return Results.Json(new MeResponse(principal, isAdmin ? "Admin" : "Member"));
        });

        // The recipe targets a connection can be created against — the connect
        // wizard's provider picker. Any authenticated user may read this.
        app.MapGet("/portal/recipes", async (HttpContext ctx, ITokenValidator validator, PortalService portal, TesseraConfig config) =>
        {
            var principal = await ResolvePrincipalAsync(ctx, validator, config).ConfigureAwait(false);
            if (principal is null)
            {
                return Results.Json(new { error = "unauthenticated" }, statusCode: 401);
            }

            return Results.Json(portal.ListRecipes()
                .Select(r => new RecipeDto(r.Provider, r.DisplayName))
                .ToArray());
        });

        // The Users view — operator-only. Lists every person + their attention rollup.
        app.MapGet("/portal/people", async (HttpContext ctx, ITokenValidator validator, PortalService portal, TesseraConfig config, CancellationToken ct) =>
        {
            var principal = await ResolvePrincipalAsync(ctx, validator, config).ConfigureAwait(false);
            if (principal is null)
            {
                return Results.Json(new { error = "unauthenticated" }, statusCode: 401);
            }

            if (!portal.IsAdmin(principal))
            {
                // Members can't enumerate everyone — default-deny on the admin surface.
                return Results.Json(new { error = "forbidden: the Users view is operator-only" }, statusCode: 403);
            }

            var people = await portal.ListPeopleAsync(ct).ConfigureAwait(false);
            return Results.Json(people.Select(ToDto).ToArray());
        });

        // Connections for a person. A member sees only their own; an admin may pass
        // ?principal= to view anyone's (the person-detail surface).
        app.MapGet("/portal/connections", async (HttpContext ctx, ITokenValidator validator, PortalService portal, TesseraConfig config, string? principal, CancellationToken ct) =>
        {
            var caller = await ResolvePrincipalAsync(ctx, validator, config).ConfigureAwait(false);
            if (caller is null)
            {
                return Results.Json(new { error = "unauthenticated" }, statusCode: 401);
            }

            var target = principal ?? caller;
            var isSelf = string.Equals(target, caller, StringComparison.OrdinalIgnoreCase);
            if (!isSelf && !portal.IsAdmin(caller))
            {
                // Cross-person reads are operator-only (and would be step-up-gated in the UI).
                return Results.Json(new { error = "forbidden: only an operator may view another person's connections" }, statusCode: 403);
            }

            var connections = await portal.ListConnectionsAsync(target, ct).ConfigureAwait(false);
            return Results.Json(connections.Select(ToDto).ToArray());
        });

        // Add (or re-point) a connection — the connect wizard's write. An admin may
        // add for anyone; a member may add only for themselves. Writes a binding
        // (the person + connection appear); authorizing a consumer is a separate
        // grant step, so the new connection is deny-by-default until granted (R3).
        app.MapPost("/portal/connections", async (
            HttpContext ctx, AddConnectionRequest? body, ITokenValidator validator, PortalService portal, TesseraConfig config, CancellationToken ct) =>
        {
            var caller = await ResolvePrincipalAsync(ctx, validator, config).ConfigureAwait(false);
            if (caller is null)
            {
                return Results.Json(new { error = "unauthenticated" }, statusCode: 401);
            }

            if (body is null
                || string.IsNullOrWhiteSpace(body.Provider)
                || string.IsNullOrWhiteSpace(body.Principal)
                || string.IsNullOrWhiteSpace(body.Credential))
            {
                return Results.Json(new { error = "bad request: provider, principal and credential are required" }, statusCode: 400);
            }

            var forSelf = string.Equals(body.Principal, caller, StringComparison.OrdinalIgnoreCase);
            if (!forSelf && !portal.IsAdmin(caller))
            {
                return Results.Json(new { error = "forbidden: only an operator may add a connection for another person" }, statusCode: 403);
            }

            var created = await portal.AddConnectionAsync(body.Provider.Trim(), body.Principal.Trim(), body.Credential.Trim(), ct).ConfigureAwait(false);
            return Results.Json(ToDto(created), statusCode: 201);
        });

        // Request a live-view handle to (re-)seed a connection's session — the
        // captcha hand-off. Fail-closed by default (no worker provider wired).
        app.MapPost("/portal/connections/{connectionId}/live-view", async (
            HttpContext ctx, string connectionId, ITokenValidator validator, PortalService portal, ILiveViewProvider liveView, TesseraConfig config, CancellationToken ct) =>
        {
            var caller = await ResolvePrincipalAsync(ctx, validator, config).ConfigureAwait(false);
            if (caller is null)
            {
                return Results.Json(new { error = "unauthenticated" }, statusCode: 401);
            }

            // The connection id is "{provider}:{principal}". A member may only seed
            // their own; an admin may seed anyone's (UI gates this behind step-up).
            var owner = OwnerOf(connectionId);
            var isSelf = owner is not null && string.Equals(owner, caller, StringComparison.OrdinalIgnoreCase);
            if (!isSelf && !portal.IsAdmin(caller))
            {
                return Results.Json(new { error = "forbidden: only the owner or an operator may seed this connection" }, statusCode: 403);
            }

            var result = await liveView.RequestAsync(connectionId, owner ?? caller, ct).ConfigureAwait(false);
            if (!result.Issued)
            {
                // Fail-closed (no worker) → 503, never a faked session.
                return Results.Json(new { error = result.Reason }, statusCode: 503);
            }

            var h = result.Handle!;
            return Results.Json(new LiveViewResponse(
                h.LiveViewUrl,
                h.Mode == LiveViewMode.ReadWrite ? "readwrite" : "readonly",
                h.SessionTtlSeconds,
                h.ExpiresAt,
                h.TargetHostname,
                h.FaviconUrl));
        });
    }

    /// <summary>
    /// Resolves the caller's principal from a verified forwarded OIDC token, or —
    /// only on a loopback bind in dev mode — from an explicit dev header. Returns
    /// null (unauthenticated) otherwise. This mirrors the broker's identity posture:
    /// a dev shortcut is tolerated only where the broker itself tolerates unverified
    /// callers (loopback dev mode), never on a real network bind.
    /// </summary>
    private static async Task<string?> ResolvePrincipalAsync(HttpContext ctx, ITokenValidator validator, TesseraConfig config)
    {
        if (config.Identity.Mode == "dev" && config.Server.IsLoopback)
        {
            var dev = ctx.Request.Headers[DevPrincipalHeader].ToString();
            if (!string.IsNullOrWhiteSpace(dev))
            {
                return dev.Trim();
            }
        }

        var auth = ctx.Request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        if (!auth.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = auth[bearer.Length..].Trim();
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var result = await validator.ValidateAsync(token, ctx.RequestAborted).ConfigureAwait(false);
        var user = result.Succeeded ? result.ToEndUserAssertion() : null;
        return user?.PreferredUsername ?? user?.Subject;
    }

    private static string? OwnerOf(string connectionId)
    {
        var idx = connectionId.IndexOf(':', StringComparison.Ordinal);
        return idx >= 0 && idx < connectionId.Length - 1 ? connectionId[(idx + 1)..] : null;
    }

    private static PersonDto ToDto(PersonView p) =>
        new(p.Principal, p.Role.ToString(), p.ConnectionCount, p.NeedsAttentionCount);

    private static ConnectionDto ToDto(PortalConnection c) =>
        new(c.ConnectionId, c.OwnerPrincipal, c.Provider, c.DisplayName, c.Status,
            c.HasCookies, c.HasRefreshToken, c.HasAccessToken, c.ExpiresAt, c.ExpiryIsEstimated);
}

/// <summary>The /portal/me payload.</summary>
internal sealed record MeResponse(string Principal, string Role);

/// <summary>Runtime auth config the SPA fetches before sign-in.</summary>
internal sealed record PortalConfigDto(string AuthMode, bool DevLoopback, OidcConfigDto? Oidc);

/// <summary>The public OIDC settings the SPA needs to start an Entra sign-in.</summary>
internal sealed record OidcConfigDto(string Authority, string ClientId, string Scope);

/// <summary>A provider option for the connect wizard's picker.</summary>
internal sealed record RecipeDto(string Provider, string DisplayName);

/// <summary>The connect-wizard write body.</summary>
internal sealed record AddConnectionRequest(string Provider, string Principal, string Credential);

/// <summary>A Users-view row (camelCase over the wire matches the web client contract).</summary>
internal sealed record PersonDto(string Principal, string Role, int ConnectionCount, int NeedsAttentionCount);

/// <summary>A connection row — presence flags + health only, never a secret value.</summary>
internal sealed record ConnectionDto(
    string ConnectionId,
    string OwnerPrincipal,
    string Provider,
    string DisplayName,
    string Status,
    bool HasCookies,
    bool HasRefreshToken,
    bool HasAccessToken,
    DateTimeOffset? ExpiresAt,
    bool ExpiryIsEstimated);

/// <summary>A live-view handle payload for the captcha hand-off.</summary>
internal sealed record LiveViewResponse(
    string LiveViewUrl,
    string Mode,
    int SessionTtlSeconds,
    DateTimeOffset ExpiresAt,
    string TargetHostname,
    string? FaviconUrl);
