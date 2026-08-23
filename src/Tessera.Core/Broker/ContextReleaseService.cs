using Tessera.Core.Audit;
using Tessera.Core.Identity;
using Tessera.Core.Kernel;
using Tessera.Core.Model;
using Tessera.Core.Policy;

namespace Tessera.Core.Broker;

public sealed record ContextReleaseRequest(
    CallerIdentity Caller,
    EndUserAssertion? OnBehalfOf,
    TrustedStateQuery StateQuery,
    ContextBuildRequest Context,
    string DisclosureReason);

public sealed record ContextReleaseResult(
    Decision Decision,
    ContextEnvelope? Envelope,
    string DisclosureReason);

public sealed class ContextReleaseService
{
    private const string ReadContextAction = "read:context";
    private readonly PolicyDecisionPoint _policy;
    private readonly TrustedStateProjection _projection;
    private readonly IAuditSink _audit;

    public ContextReleaseService(
        PolicyDecisionPoint policy,
        TrustedStateProjection projection,
        IAuditSink? audit = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _projection = projection ?? throw new ArgumentNullException(nameof(projection));
        _audit = audit ?? NullAuditSink.Instance;
    }

    public async Task<ContextReleaseResult> ReleaseAsync(
        ContextReleaseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Caller);
        ArgumentNullException.ThrowIfNull(request.StateQuery);
        ArgumentNullException.ThrowIfNull(request.Context);
        var reason = KernelValidation.Text(request.DisclosureReason, nameof(request.DisclosureReason), 1024);
        var owner = request.StateQuery.OwnerPrincipalId;
        var access = new AccessRequest(
            request.Caller,
            $"context:{owner}",
            ReadContextAction,
            request.OnBehalfOf);
        var delegatedOwner = request.OnBehalfOf?.CanonicalPrincipalId;
        Decision decision;
        if (!string.Equals(request.Context.OwnerPrincipalId, owner, StringComparison.Ordinal)
            || !string.Equals(delegatedOwner, owner, StringComparison.Ordinal))
        {
            decision = Decision.Deny("context owner does not match the verified delegated principal");
        }
        else
        {
            decision = _policy.Evaluate(access);
        }

        _audit.Record(access, decision, null);
        if (!decision.Allowed)
        {
            return new ContextReleaseResult(decision, null, reason);
        }

        _ = ContextBuilder.Build(request.Context, []);
        var snapshot = await _projection.ProjectAsync(request.StateQuery, cancellationToken);
        var items = snapshot.Entries.SelectMany(ToContextItems).ToArray();
        var envelope = ContextBuilder.Build(request.Context, items);
        return new ContextReleaseResult(decision, envelope, reason);
    }

    private static IEnumerable<ContextItem> ToContextItems(TrustedStateEntry entry)
    {
        if (entry.Current is { } current)
        {
            yield return ToContextItem(entry, current, ContextItemKind.CurrentFact, 1m);
        }

        foreach (var conflict in entry.Conflicts)
        {
            yield return ToContextItem(entry, conflict, ContextItemKind.UncertainAssertion, 0.8m);
        }
    }

    private static ContextItem ToContextItem(
        TrustedStateEntry entry,
        TrustedAssertion assertion,
        ContextItemKind kind,
        decimal relevance)
    {
        var evidence = entry.Evidence
            .Where(item => assertion.EvidenceRefs.Contains(item.EvidenceId, StringComparer.Ordinal))
            .ToArray();
        var sensitivity = evidence.Length == 0
            ? SensitivityClass.Internal
            : evidence.Max(item => item.Sensitivity);
        var provenance = assertion.EvidenceRefs
            .Concat(assertion.LineageRefs)
            .Append(assertion.AssertionId)
            .Distinct(StringComparer.Ordinal);
        return ContextItem.Create(
            assertion.AssertionId,
            kind,
            $"{entry.Key.SubjectKey}.{entry.Key.Predicate} = {assertion.Value}",
            sensitivity,
            relevance,
            assertion.Assertion.ValidFrom,
            provenance);
    }
}