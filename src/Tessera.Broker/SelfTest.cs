using Tessera.Core.Broker;
using Tessera.Core.Identity;
using Tessera.Core.Model;

namespace Tessera.Broker;

/// <summary>The secret-free result of the startup self-test.</summary>
/// <param name="Target">The target resolved.</param>
/// <param name="OnBehalfOf">The principal, if any.</param>
/// <param name="Effect">The policy effect.</param>
/// <param name="Reason">The decision reason.</param>
/// <param name="CredentialStatus">The resolved credential status, or <c>null</c>.</param>
/// <param name="CredentialDetail">The secret-free credential detail, or <c>null</c>.</param>
/// <param name="Ok">True when allowed and a usable credential resolved.</param>
public sealed record SelfTestResult(
    string Target,
    string? OnBehalfOf,
    string Effect,
    string Reason,
    string? CredentialStatus,
    string? CredentialDetail,
    bool Ok);

/// <summary>
/// The startup self-test: exercise the authorize+resolve spine against the real
/// store, secret-free. It builds an internally-trusted request (the caller is
/// constructed here, never read from the network) and reports only a status — it
/// makes <em>no</em> upstream call, so running it against a real account is
/// side-effect-free (review H3: report, never re-login).
/// </summary>
public static class SelfTest
{
    /// <summary>Runs the self-test for a target (+ optional principal).</summary>
    public static async Task<SelfTestResult> RunAsync(
        BrokerCore broker,
        string target,
        string? principal,
        string trustDomain,
        CancellationToken cancellationToken = default)
    {
        var caller = new CallerIdentity(
            $"spiffe://{trustDomain}/selftest",
            VerificationMethod.SpiffeSvid,
            trustDomain);

        var onBehalfOf = principal is null
            ? null
            : new EndUserAssertion(principal, "tessera-selftest", VerificationMethod.OidcJwt, principal);

        var request = new AccessRequest(caller, target, "read:selftest", onBehalfOf);
        var result = await broker.HandleAsync(request, cancellationToken).ConfigureAwait(false);

        return new SelfTestResult(
            Target: target,
            OnBehalfOf: principal,
            Effect: result.Decision.Effect.ToString().ToLowerInvariant(),
            Reason: result.Decision.Reason,
            CredentialStatus: result.Credential?.Status.ToString().ToLowerInvariant(),
            CredentialDetail: result.Credential?.Detail,
            Ok: result.Ok);
    }
}

/// <summary>Mutable, secret-free runtime status shared with the HTTP endpoints.</summary>
public sealed class BrokerStatus
{
    /// <summary>True once startup wiring completed.</summary>
    public bool Ready { get; set; }

    /// <summary>A short description of the backing store.</summary>
    public string StoreKind { get; set; } = "unknown";

    /// <summary>
    /// Whether the caller plane (<c>/v1/broker</c>) is enabled for this deployment —
    /// it opens once a caller authenticator is configured (<c>identity.mode=oidc</c> +
    /// an audience; ADR 0021), and stays fail-closed otherwise.
    /// </summary>
    public bool BrokerEndpointEnabled { get; set; }

    /// <summary>Whether OIDC delegation is enforceable (an audience is configured — gate G2).</summary>
    public bool DelegationEnabled { get; set; }

    /// <summary>The startup self-test result, if one ran.</summary>
    public SelfTestResult? SelfTest { get; set; }
}
