using Tessera.Core.Audit;
using Tessera.Core.Model;
using Tessera.Core.Policy;
using Tessera.Core.Resolution;

namespace Tessera.Core.Broker;

/// <summary>
/// The broker core — authorize, then (secretlessly) resolve the credential's
/// status. This is the whole pipeline minus the network and the upstream call:
/// PDP → on allow, resolve status → audit → <see cref="BrokerResult"/>.
/// </summary>
/// <remarks>
/// It deliberately does <em>not</em> make the outbound call to the upstream
/// service — that "injection egress" lives in the broker host and is gated, so
/// deploying the broker never opens an unauthenticated path to a real account.
/// The store is touched only for an authorized request.
/// </remarks>
public sealed class BrokerCore
{
    private readonly PolicyDecisionPoint _pdp;
    private readonly CredentialResolver _resolver;
    private readonly IAuditSink _audit;

    /// <summary>Wires the PDP, resolver, and audit sink into one decision pipeline.</summary>
    public BrokerCore(PolicyDecisionPoint pdp, CredentialResolver resolver, IAuditSink? audit = null)
    {
        _pdp = pdp;
        _resolver = resolver;
        _audit = audit ?? NullAuditSink.Instance;
    }

    /// <summary>Authorizes a request and, on allow, resolves the credential status.</summary>
    public async Task<BrokerResult> HandleAsync(AccessRequest request, CancellationToken cancellationToken = default)
    {
        var decision = _pdp.Evaluate(request);

        ResolvedCredential? credential = null;
        if (decision.Allowed)
        {
            // Only touch the store once the request is authorized.
            credential = await _resolver.ResolveAsync(request, cancellationToken).ConfigureAwait(false);
        }

        _audit.Record(request, decision, credential);
        return new BrokerResult(decision, credential);
    }
}
