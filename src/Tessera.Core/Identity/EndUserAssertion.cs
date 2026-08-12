namespace Tessera.Core.Identity;

/// <summary>
/// A human on whose behalf a caller acts — the optional "FOR WHOM". Absent for
/// pure automation, which acts only as itself (ADR 0009).
/// </summary>
/// <remarks>
/// The <paramref name="Subject"/> is the stable per-user key (for Entra, the
/// <c>oid</c> claim; <see cref="PreferredUsername"/> carries the human-readable
/// <c>preferred_username</c> for audit). An assertion is only
/// <see cref="IsVerified"/> when it arrived as a validated signed token
/// (ADR 0005); a plaintext "on behalf of X" is never verified.
/// </remarks>
/// <param name="Subject">Stable end-user identifier (e.g. Entra <c>oid</c>).</param>
/// <param name="Issuer">The token issuer that vouched for the subject.</param>
/// <param name="VerifiedVia">How the assertion was proven (default: a signed OIDC JWT).</param>
/// <param name="PreferredUsername">Human-readable username for audit, if known.</param>
/// <param name="TenantId">Immutable tenant context from the signed token.</param>
public sealed record EndUserAssertion(
    string Subject,
    string Issuer,
    VerificationMethod VerifiedVia = VerificationMethod.OidcJwt,
    string? PreferredUsername = null,
    string? TenantId = null)
{
    /// <summary>True when the assertion was cryptographically verified.</summary>
    public bool IsVerified => VerifiedVia.IsVerified();

    /// <summary>
    /// Canonical authorization key. Null means the signed assertion lacks tenant
    /// context and cannot own newly persisted personal state.
    /// </summary>
    public string? CanonicalPrincipalId => string.IsNullOrWhiteSpace(TenantId)
        ? null
        : Kernel.PrincipalRef.Create(
            Issuer,
            TenantId,
            Subject,
            PreferredUsername,
            DateTimeOffset.UnixEpoch).PrincipalId;
}
