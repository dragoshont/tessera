namespace Tessera.Core.Identity;

/// <summary>
/// A workload (non-human identity) that is making a request — the "WHO".
/// </summary>
/// <remarks>
/// <para>
/// <paramref name="Id"/> is a stable identifier: a SPIFFE ID
/// (<c>spiffe://domain/workload</c>), a certificate subject, or — for an app-only
/// Entra token — the application id (<c>appid</c>/<c>azp</c>).
/// </para>
/// <para>
/// <paramref name="VerifiedVia"/> records <em>how</em> the identity was proven; the
/// policy layer refuses unverified callers outside loopback dev (ADR 0005).
/// </para>
/// </remarks>
/// <param name="Id">Stable caller identifier (SPIFFE ID / cert subject / appid).</param>
/// <param name="VerifiedVia">How the caller was authenticated.</param>
/// <param name="TrustDomain">Optional trust domain the caller belongs to.</param>
public sealed record CallerIdentity(
    string Id,
    VerificationMethod VerifiedVia,
    string? TrustDomain = null)
{
    /// <summary>True when the caller was cryptographically verified.</summary>
    public bool IsVerified => VerifiedVia.IsVerified();
}
