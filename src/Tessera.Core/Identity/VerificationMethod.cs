namespace Tessera.Core.Identity;

/// <summary>
/// How an identity was established. <see cref="Dev"/> is unverified — tolerated
/// only on a loopback dev listener, never on the network (ADR 0005).
/// </summary>
public enum VerificationMethod
{
    /// <summary>Unverified. Local development only; the PDP denies it off loopback.</summary>
    Dev = 0,

    /// <summary>Mutual-TLS client certificate.</summary>
    Mtls,

    /// <summary>SPIFFE X.509-SVID.</summary>
    SpiffeSvid,

    /// <summary>A signed OIDC / JWT assertion (validated: sig, aud, exp, iss, tid).</summary>
    OidcJwt,

    /// <summary>
    /// Trusted by the network boundary: the chat→Tessera hop is NetworkPolicy-gated
    /// (only the chat workload can reach the broker) and a valid end-user OIDC token
    /// accompanies the call. Used for the shared chat caller, which presents no SVID
    /// (review C2 / ADR 0005).
    /// </summary>
    Network,
}

/// <summary>Extensions over <see cref="VerificationMethod"/>.</summary>
public static class VerificationMethodExtensions
{
    /// <summary>True when the method cryptographically proves the identity.</summary>
    public static bool IsVerified(this VerificationMethod method) => method != VerificationMethod.Dev;
}
