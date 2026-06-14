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
