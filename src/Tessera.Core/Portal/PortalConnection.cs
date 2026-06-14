namespace Tessera.Core.Portal;

/// <summary>
/// One connection as the admin portal sees it (ADR 0016 / admin-portal spec §8).
/// A connection is a <c>(provider, person)</c> pairing backed by a stored session —
/// the portal lists these, shows their health, and acts on them. It carries
/// <em>presence</em> flags and metadata only; <b>no secret value is ever part of
/// this projection</b> ("Tessera can't show this — that's the point").
/// </summary>
/// <param name="ConnectionId">Stable id for the (provider, person) pair.</param>
/// <param name="OwnerPrincipal">The person this connection acts on behalf of.</param>
/// <param name="Provider">The provider/target (the recipe target).</param>
/// <param name="DisplayName">A human label (recipe description, else the target).</param>
/// <param name="Status">UI health: <c>live | expiring_soon | absent | error | seeding | needs_human</c>.</param>
/// <param name="HasCookies">Whether session cookies are present (presence, not value).</param>
/// <param name="HasRefreshToken">Whether a refresh token is present.</param>
/// <param name="HasAccessToken">Whether an access token is present.</param>
/// <param name="ExpiresAt">When the session expires, if knowable (often null — cookies carry no readable TTL).</param>
/// <param name="ExpiryIsEstimated">True when <see cref="ExpiresAt"/> is an estimate or unknown — surface "~estimated".</param>
/// <param name="Detail">A secret-free explanation of the status.</param>
public sealed record PortalConnection(
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
    string Detail = "");

/// <summary>A person plus the portal's attention rollup (Users-view row).</summary>
/// <param name="Principal">The verified principal (e.g. <c>alice@example.com</c>).</param>
/// <param name="Role">Admin (in the allow-list) or Member.</param>
/// <param name="ConnectionCount">How many connections act on this person's behalf.</param>
/// <param name="NeedsAttentionCount">How many of those are not healthy (absent/error/expiring).</param>
public sealed record PersonView(
    string Principal,
    PortalRole Role,
    int ConnectionCount,
    int NeedsAttentionCount);

/// <summary>A provider a connection can be created against — the connect wizard's
/// provider picker. A recipe target + its human label, never any secret.</summary>
/// <param name="Provider">The recipe target (e.g. <c>health-portal</c>).</param>
/// <param name="DisplayName">The human label (the recipe description, else the target).</param>
public sealed record RecipeSummary(string Provider, string DisplayName);

/// <summary>
/// One delegation as the awareness dashboard sees it (ADR 0017) — a projection of a
/// single <c>grant</c>: <em>who</em> (a caller workload) may act <em>as whom</em>
/// (<see cref="OnBehalfOf"/>) on <em>what</em> (<see cref="Target"/>), with which
/// <see cref="Actions"/>, and which of those need a human step-up. It answers
/// "who/what may act as me?" — the consent/transparency view. Secret-free.
/// </summary>
/// <param name="Caller">The caller workload the grant authorizes (e.g. a SPIFFE id or MCP id).</param>
/// <param name="Target">The target/provider the grant applies to.</param>
/// <param name="DisplayName">A human label (recipe description, else the target).</param>
/// <param name="Actions">The action globs the caller may perform.</param>
/// <param name="StepUpActions">The action globs that require a human step-up first.</param>
/// <param name="IsAutomation">True when the grant is pure automation (no delegated human).</param>
/// <param name="OnBehalfOf">The delegated principal, or <c>null</c> for automation.</param>
public sealed record DelegationView(
    string Caller,
    string Target,
    string DisplayName,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> StepUpActions,
    bool IsAutomation,
    string? OnBehalfOf);
