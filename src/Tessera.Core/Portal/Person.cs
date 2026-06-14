namespace Tessera.Core.Portal;

/// <summary>
/// A person's role in the admin portal. There are exactly two: a normal
/// <see cref="Member"/> (sees only their own connections) and an
/// <see cref="Admin"/> (the operator surface). The role is decided by a small
/// config allow-list — there is no database (ADR 0016 / spec §7b).
/// </summary>
public enum PortalRole
{
    /// <summary>A normal person: sees only their own connections.</summary>
    Member,

    /// <summary>An operator: may enter the "All connections" surface (behind step-up).</summary>
    Admin,
}

/// <summary>
/// One person the portal knows about, derived — never stored. A person is a
/// <em>verified OIDC principal</em> (an <c>oid</c> or <c>preferred_username</c>)
/// that already appears in the policy files as a delegated <c>onBehalfOf</c>, or
/// that is named in the admins allow-list. Their <see cref="Role"/> is decided by
/// that allow-list; their <see cref="ConnectionCount"/> is how many bindings back
/// them. No secret value is ever part of this projection.
/// </summary>
/// <param name="Principal">The verified principal (e.g. <c>alice@example.com</c>).</param>
/// <param name="Role">Admin (in the allow-list) or Member.</param>
/// <param name="ConnectionCount">Bindings (connections) that act on this person's behalf.</param>
public sealed record Person(
    string Principal,
    PortalRole Role,
    int ConnectionCount);
