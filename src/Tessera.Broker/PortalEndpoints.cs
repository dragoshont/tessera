using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Tessera.Core.Audit;
using Tessera.Core.Configuration;
using Tessera.Core.Model;
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

        // The activity feed (ADR 0017) — a secret-free, bounded tail of brokering
        // decisions, plus a small rollup. Self-scoped by default: a member sees only
        // decisions made on their behalf. An operator may pass ?principal= to view
        // one person, or omit it to see everyone. ?limit caps the rows (clamped to the
        // ring capacity); ?since is an ISO-8601 lower bound. The summary spans the
        // whole scoped window so the counts are honest even when the rows are capped.
        app.MapGet("/portal/audit", async (
            HttpContext ctx, ITokenValidator validator, PortalService portal, IAuditTail tail, TesseraConfig config,
            string? principal, int? limit, string? since, CancellationToken ct) =>
        {
            var caller = await ResolvePrincipalAsync(ctx, validator, config).ConfigureAwait(false);
            if (caller is null)
            {
                return Results.Json(new { error = "unauthenticated" }, statusCode: 401);
            }

            // Scope server-side (never a client filter). Operator: ?principal= picks a
            // person, omitted = everyone (null scope). Member: forced to self; asking
            // for someone else is forbidden, not silently re-scoped.
            string? scope;
            if (portal.IsAdmin(caller))
            {
                scope = string.IsNullOrWhiteSpace(principal) ? null : principal.Trim();
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(principal)
                    && !string.Equals(principal.Trim(), caller, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Json(new { error = "forbidden: a member may only read their own activity" }, statusCode: 403);
                }

                scope = caller;
            }

            var capacity = Math.Max(tail.Capacity, 1);
            var rowLimit = Math.Clamp(limit ?? 100, 1, capacity);
            var sinceTs = ParseSince(since);

            // One bounded query over the whole scoped window; the summary spans all of
            // it, the rows show the freshest `rowLimit`.
            var window = tail.Query(scope, sinceTs, capacity);
            var rows = window.Take(rowLimit).Select(ToAuditRow).ToArray();
            var summary = BuildAuditSummary(window);
            return Results.Json(new AuditFeedDto(rows, summary));
        });

        // Delegations (ADR 0017) — "who/what may act as me". A projection of the
        // enforced grants. Self-scoped by default (a member sees only grants that
        // delegate to them); an operator may pass ?principal= for one person or omit
        // it to see every grant (including pure automation). Secret-free.
        app.MapGet("/portal/delegations", async (
            HttpContext ctx, ITokenValidator validator, PortalService portal, TesseraConfig config, string? principal) =>
        {
            var caller = await ResolvePrincipalAsync(ctx, validator, config).ConfigureAwait(false);
            if (caller is null)
            {
                return Results.Json(new { error = "unauthenticated" }, statusCode: 401);
            }

            string? scope;
            if (portal.IsAdmin(caller))
            {
                scope = string.IsNullOrWhiteSpace(principal) ? null : principal.Trim();
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(principal)
                    && !string.Equals(principal.Trim(), caller, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Json(new { error = "forbidden: a member may only see who can act as themselves" }, statusCode: 403);
                }

                scope = caller;
            }

            var delegations = portal.ListDelegations(scope);
            return Results.Json(delegations.Select(ToDelegationDto).ToArray());
        });

        // Dependents (ADR 0020) — "whose accounts do I manage?". The dependents the
        // caller may act as, derived from the owner: dependent bindings they seeded.
        // Self-scoped; an operator may pass a principal. Secret-free; confers no
        // authority (the PDP grants still gate every action).
        app.MapGet("/portal/dependents", async (
            HttpContext ctx, ITokenValidator validator, PortalService portal, TesseraConfig config, string? principal) =>
        {
            var caller = await ResolvePrincipalAsync(ctx, validator, config).ConfigureAwait(false);
            if (caller is null)
            {
                return Results.Json(new { error = "unauthenticated" }, statusCode: 401);
            }

            string guardian;
            if (portal.IsAdmin(caller))
            {
                guardian = string.IsNullOrWhiteSpace(principal) ? caller : principal.Trim();
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(principal)
                    && !string.Equals(principal.Trim(), caller, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Json(new { error = "forbidden: a member may only see their own dependents" }, statusCode: 403);
                }

                guardian = caller;
            }

            return Results.Json(new { guardian, dependents = portal.ListDependents(guardian) });
        });

        // Consents (ADR 0020) — "what data classes did I consent to, and when?". The
        // receipts captured when a person seeds a user/dependent-owned connection.
        // Self-scoped; an operator may pass a principal. Secret-free (no value, just
        // who/what/when/ownership).
        app.MapGet("/portal/consents", async (
            HttpContext ctx, ITokenValidator validator, PortalService portal, TesseraConfig config, string? principal) =>
        {
            var caller = await ResolvePrincipalAsync(ctx, validator, config).ConfigureAwait(false);
            if (caller is null)
            {
                return Results.Json(new { error = "unauthenticated" }, statusCode: 401);
            }

            string subject;
            if (portal.IsAdmin(caller))
            {
                subject = string.IsNullOrWhiteSpace(principal) ? caller : principal.Trim();
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(principal)
                    && !string.Equals(principal.Trim(), caller, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Json(new { error = "forbidden: a member may only see their own consents" }, statusCode: 403);
                }

                subject = caller;
            }

            return Results.Json(portal.ListConsents(subject).Select(ToConsentDto).ToArray());
        });

        // Modules (ADR 0017) — "what connectors are loaded". The catalog of recipes
        // plus the broker's egress posture and a per-module connection count. Any
        // authenticated user may read it (the catalog is shared, like /portal/recipes);
        // the count is a non-sensitive aggregate (no owners). Secret-free: the upstream
        // host only, never a path or credential.
        app.MapGet("/portal/modules", async (
            HttpContext ctx, ITokenValidator validator, PortalService portal, TesseraConfig config) =>
        {
            var principal = await ResolvePrincipalAsync(ctx, validator, config).ConfigureAwait(false);
            if (principal is null)
            {
                return Results.Json(new { error = "unauthenticated" }, statusCode: 401);
            }

            var modules = portal.ListModules(config.Egress.Enabled);
            return Results.Json(modules.Select(ToModuleDto).ToArray());
        });

        // The rotation schedule of one connection (ADR 0017) — "is an automatic job
        // keeping this session warm, and who owns it?" Authorized like live-view: the
        // owner or an operator. Honest ownership (none|external|tessera); last/next-run
        // appear only once Tessera itself owns rotation (Mode U). 404 if no such
        // connection exists.
        app.MapGet("/portal/connections/{connectionId}/schedule", async (
            HttpContext ctx, string connectionId, ITokenValidator validator, PortalService portal, TesseraConfig config) =>
        {
            var caller = await ResolvePrincipalAsync(ctx, validator, config).ConfigureAwait(false);
            if (caller is null)
            {
                return Results.Json(new { error = "unauthenticated" }, statusCode: 401);
            }

            var owner = OwnerOf(connectionId);
            var isSelf = owner is not null && string.Equals(owner, caller, StringComparison.OrdinalIgnoreCase);
            if (!isSelf && !portal.IsAdmin(caller))
            {
                return Results.Json(new { error = "forbidden: only the owner or an operator may view this connection's schedule" }, statusCode: 403);
            }

            var schedule = portal.GetSchedule(connectionId);
            if (schedule is null)
            {
                return Results.Json(new { error = "not found: no such connection" }, statusCode: 404);
            }

            return Results.Json(ToScheduleDto(schedule));
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

            var created = await portal.AddConnectionAsync(body.Provider.Trim(), body.Principal.Trim(), body.Credential.Trim(), cancellationToken: ct).ConfigureAwait(false);
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

    /// <summary>Parses an ISO-8601 <c>?since</c> bound to UTC; null when absent or unparseable.</summary>
    private static DateTimeOffset? ParseSince(string? since) =>
        !string.IsNullOrWhiteSpace(since)
        && DateTimeOffset.TryParse(
            since,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var ts)
            ? ts
            : null;

    /// <summary>Maps the decision effect to the wire vocabulary (<c>allow|deny|step-up</c>).</summary>
    private static string EffectString(Effect effect) => effect switch
    {
        Effect.Allow => "allow",
        Effect.StepUp => "step-up",
        _ => "deny",
    };

    private static AuditRowDto ToAuditRow(AuditEntry e) =>
        new(e.Timestamp, e.Caller, e.CallerVerified, e.OnBehalfOf, e.Target, e.Action, EffectString(e.Effect), e.Reason, e.CredentialStatus);

    /// <summary>Builds the rollup over a scoped audit window (counts + breakdowns + span).</summary>
    private static AuditSummaryDto BuildAuditSummary(IReadOnlyList<AuditEntry> window)
    {
        var allow = 0;
        var deny = 0;
        var stepUp = 0;
        var byTarget = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var byCaller = new Dictionary<string, int>(StringComparer.Ordinal);
        DateTimeOffset? since = null;
        DateTimeOffset? until = null;

        foreach (var e in window)
        {
            switch (e.Effect)
            {
                case Effect.Allow: allow++; break;
                case Effect.StepUp: stepUp++; break;
                default: deny++; break;
            }

            byTarget[e.Target] = byTarget.GetValueOrDefault(e.Target) + 1;
            byCaller[e.Caller] = byCaller.GetValueOrDefault(e.Caller) + 1;
            if (since is null || e.Timestamp < since) { since = e.Timestamp; }
            if (until is null || e.Timestamp > until) { until = e.Timestamp; }
        }

        return new AuditSummaryDto(window.Count, allow, deny, stepUp, byTarget, byCaller, since, until);
    }

    private static PersonDto ToDto(PersonView p) =>
        new(p.Principal, p.Role.ToString(), p.ConnectionCount, p.NeedsAttentionCount);

    private static DelegationDto ToDelegationDto(DelegationView d) =>
        new(d.Caller, d.Target, d.DisplayName, d.Actions, d.StepUpActions, d.Planes, d.IsAutomation, d.OnBehalfOf, d.Owner);

    private static ModuleDto ToModuleDto(ModuleView m) =>
        new(m.Target, m.DisplayName, m.Driver, m.Egress, m.EgressEnabled, m.Actions, m.Planes, m.ToolCount, m.ConnectionCount, m.UpstreamHost);

    private static ScheduleDto ToScheduleDto(ScheduleView s) =>
        new(s.ConnectionId, s.RotationOwner, s.RefreshConfigured, s.Detail, s.LastRotatedAt, s.NextRotationAt);

    private static ConnectionDto ToDto(PortalConnection c) =>
        new(c.ConnectionId, c.OwnerPrincipal, c.Provider, c.DisplayName, c.Status,
            c.HasCookies, c.HasRefreshToken, c.HasAccessToken, c.ExpiresAt, c.ExpiryIsEstimated, c.Owner, c.Guardian);

    private static ConsentDto ToConsentDto(Tessera.Core.Results.ConsentReceipt c) =>
        new(c.Principal, c.Target, c.DataClass, Tessera.Core.Resolution.CredentialOwners.ToToken(c.Owner), c.GrantedAt, c.Guardian, c.CoveredScopes);
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

/// <summary>A consent receipt row (ADR 0020): who consented to what data class, when, under which ownership.</summary>
internal sealed record ConsentDto(
    string Principal,
    string Target,
    string DataClass,
    string Owner,
    DateTimeOffset GrantedAt,
    string? Guardian,
    IReadOnlyList<string> Scopes);

/// <summary>A Users-view row (camelCase over the wire matches the web client contract).</summary>
internal sealed record PersonDto(string Principal, string Role, int ConnectionCount, int NeedsAttentionCount);

/// <summary>A delegation row (ADR 0017): who/what may act as a person, on what, and what needs step-up.</summary>
internal sealed record DelegationDto(
    string Caller,
    string Target,
    string DisplayName,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> StepUpActions,
    IReadOnlyList<string> Planes,
    bool IsAutomation,
    string? OnBehalfOf,
    string? Owner);

/// <summary>A module row (ADR 0017): a loaded connector + its egress posture + usage count.</summary>
internal sealed record ModuleDto(
    string Target,
    string DisplayName,
    string Driver,
    string Egress,
    bool EgressEnabled,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> Planes,
    int ToolCount,
    int ConnectionCount,
    string? UpstreamHost);

/// <summary>A connection's rotation schedule (ADR 0017): who owns rotation + honest run times.</summary>
internal sealed record ScheduleDto(
    string ConnectionId,
    string RotationOwner,
    bool RefreshConfigured,
    string Detail,
    DateTimeOffset? LastRotatedAt,
    DateTimeOffset? NextRotationAt);

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
    bool ExpiryIsEstimated,
    string Owner,
    string? Guardian);

/// <summary>A live-view handle payload for the captcha hand-off.</summary>
internal sealed record LiveViewResponse(
    string LiveViewUrl,
    string Mode,
    int SessionTtlSeconds,
    DateTimeOffset ExpiresAt,
    string TargetHostname,
    string? FaviconUrl);

/// <summary>The activity feed (ADR 0017): newest-first rows + a scoped rollup.</summary>
internal sealed record AuditFeedDto(IReadOnlyList<AuditRowDto> Entries, AuditSummaryDto Summary);

/// <summary>One secret-free activity row (ids + enums only).</summary>
internal sealed record AuditRowDto(
    DateTimeOffset Timestamp,
    string Caller,
    bool CallerVerified,
    string? OnBehalfOf,
    string Target,
    string Action,
    string Effect,
    string Reason,
    string? CredentialStatus);

/// <summary>The activity rollup over the scoped window.</summary>
internal sealed record AuditSummaryDto(
    int Total,
    int Allow,
    int Deny,
    int StepUp,
    IReadOnlyDictionary<string, int> ByTarget,
    IReadOnlyDictionary<string, int> ByCaller,
    DateTimeOffset? Since,
    DateTimeOffset? Until);
