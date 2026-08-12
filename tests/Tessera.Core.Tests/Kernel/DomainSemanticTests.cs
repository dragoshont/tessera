using System.Text;
using Tessera.Core.Kernel;
using Xunit;

namespace Tessera.Core.Tests.Kernel;

public sealed class DomainSemanticTests
{
    private const string Owner = "principal:sha256:owner";
    private static readonly DateTimeOffset T0 = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly ProducerRef Producer = ProducerRef.Create("kernel-test", "1");

    [Fact]
    public void Evidence_rejects_an_unbounded_excerpt()
    {
        Assert.Throws<ArgumentException>(() => Evidence(new string('x', 4097)));
    }

    [Fact]
    public void Evidence_rejects_obvious_secret_material()
    {
        Assert.Throws<ArgumentException>(() => Evidence(
            "Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123456789"));
    }

    [Fact]
    public void User_correction_supersedes_inference_without_erasing_provenance()
    {
        var inferred = Assertion("a-old", "15:00", AssertionType.Inferred, EpistemicStatus.Candidate, "e-old");
        var current = AssertionService.PromoteCandidate(inferred, "trusted extraction rule");
        var correction = Assertion("a-new", "16:30", AssertionType.UserAsserted, EpistemicStatus.Candidate, "e-correction");

        var changed = AssertionService.Correct(current, correction, T0.AddHours(1), "explicit user correction");

        Assert.Equal(EpistemicStatus.Superseded, changed.Superseded.EpistemicStatus);
        Assert.Equal("e-old", Assert.Single(changed.Superseded.EvidenceRefs));
        Assert.Equal(EpistemicStatus.Current, changed.Current.EpistemicStatus);
        Assert.Equal(AssertionType.UserAsserted, changed.Current.AssertionType);
        Assert.Equal("e-correction", Assert.Single(changed.Current.EvidenceRefs));
    }

    [Fact]
    public void Incompatible_assertions_become_explicitly_conflicted()
    {
        var first = Assertion("a-1", "15:00", AssertionType.SourceAsserted, EpistemicStatus.Supported, "e-1");
        var second = Assertion("a-2", "16:30", AssertionType.SourceAsserted, EpistemicStatus.Supported, "e-2");

        var conflict = AssertionService.MarkConflict(first, second, "credible sources disagree");

        Assert.Equal(EpistemicStatus.Conflicted, conflict.First.EpistemicStatus);
        Assert.Equal(EpistemicStatus.Conflicted, conflict.Second.EpistemicStatus);
    }

    [Fact]
    public void Assertion_rejects_inverted_temporal_interval()
    {
        Assert.Throws<ArgumentException>(() => AssertionRecord.Create(
            "a-invalid",
            Owner,
            "appointment-1",
            "start-time",
            "15:00",
            AssertionType.SourceAsserted,
            EpistemicStatus.Candidate,
            0.9m,
            T0.AddHours(1),
            T0,
            T0,
            null,
            ["evidence-1"],
            [],
            null,
            Producer,
            1));
    }

    [Fact]
    public void Supersession_time_matches_the_descendants_own_acceptance_evidence()
    {
        var firstAcceptance = T0.AddHours(1);
        var secondAcceptance = T0.AddHours(2);
        var original = FollowUpRevision.Create(
            "revision-old",
            FollowUpField.Deliverable,
            "old checklist",
            FollowUpRevisionState.Superseded,
            FollowUpFieldProvenance.Create(["evidence-old"], T0, "parser.v1", 0.9m),
            T0);
        var descendant = FollowUpRevision.Create(
            "revision-new",
            FollowUpField.Deliverable,
            "new checklist",
            FollowUpRevisionState.Current,
            FollowUpFieldProvenance.Create(
                ["evidence-new", "evidence-accept-second"],
                T0.AddMinutes(30),
                "parser.v1",
                0.9m,
                lineageRevisionRefs: [original.RevisionId]),
            T0.AddMinutes(30));
        var followUp = FollowUp.Create(
            "followup-selective-acceptance",
            Owner,
            FollowUpStatus.Tracked,
            [original, descendant],
            [
                FollowUpTimelineEntry.Create(
                    1,
                    FollowUpTimelineKind.Accepted,
                    null,
                    "Accepted another candidate.",
                    "evidence-accept-first",
                    firstAcceptance,
                    firstAcceptance),
                FollowUpTimelineEntry.Create(
                    2,
                    FollowUpTimelineKind.Accepted,
                    null,
                    "Accepted this candidate.",
                    "evidence-accept-second",
                    secondAcceptance,
                    secondAcceptance),
            ],
            T0,
            secondAcceptance,
            2);

        Assert.Equal(
            secondAcceptance,
            FollowUpContinuityService.FindSupersededAt(followUp, original));
    }

    [Fact]
    public void Action_rejects_invalid_transition_and_keeps_execution_distinct_from_verification()
    {
        var proposed = Action();
        Assert.Throws<InvalidOperationException>(() => proposed.TransitionTo(ActionState.ProviderVerified, T0));

        var succeeded = proposed
            .TransitionTo(ActionState.Authorized, T0, authorizationRef: "auth-1")
            .TransitionTo(ActionState.Started, T0.AddMinutes(1))
            .TransitionTo(ActionState.ExecutionSucceeded, T0.AddMinutes(2), providerReceipt: "accepted");

        Assert.Equal(ActionState.ExecutionSucceeded, succeeded.State);
        Assert.NotEqual(ActionState.ProviderVerified, succeeded.State);
        Assert.Equal("stable-idempotency-key", succeeded.IdempotencyKey);
    }

    [Fact]
    public async Task Authorization_is_exact_expiring_and_one_time()
    {
        var repository = new MemoryAuthorizationRepository();
        var service = new ActionAuthorizationService(repository);
        var action = Action();
        var authorization = await service.IssueAsync(action, T0, T0.AddMinutes(10));

        Assert.Null(await Authorize(
            service,
            action,
            authorization,
            payloadHash: ActionPayloadHash.Compute("swapped"u8),
            now: T0.AddMinutes(1)));
        Assert.Null(await Authorize(
            service,
            action,
            authorization,
            targetScope: "calendar/other",
            now: T0.AddMinutes(1)));
        var authorized = await Authorize(service, action, authorization, now: T0.AddMinutes(1));
        Assert.NotNull(authorized);
        Assert.Equal(ActionState.Authorized, authorized.State);
        Assert.Null(await Authorize(service, action, authorization, now: T0.AddMinutes(2)));

        var expired = await service.IssueAsync(action, T0, T0.AddMinutes(1));
        Assert.Null(await Authorize(service, action, expired, now: T0.AddMinutes(1)));
    }

    private static EvidenceRecord Evidence(string excerpt = "Appointment is Tuesday at 15:00.")
        => EvidenceRecord.Create(
            "evidence-1", Owner, "calendar", "native-1", "calendar://native-1", T0, T0,
            "SHA-256", 1, ActionPayloadHash.Compute(Encoding.UTF8.GetBytes(excerpt)),
            RetentionState.Active, SensitivityClass.Confidential, Producer, 1, excerpt);

    private static AssertionRecord Assertion(
        string id,
        string value,
        AssertionType type,
        EpistemicStatus status,
        string evidenceId)
        => AssertionRecord.Create(
            id, Owner, "appointment-1", "start-time", value, type, status, 0.9m,
            T0, null, T0, null, [evidenceId], [], null, Producer, 1);

    private static ActionRecord Action()
        => ActionRecord.Create(
            "action-1", Owner, "calendar.update", "1", "move appointment",
            ActionPayloadHash.Compute("payload"u8), "calendar/appointment-1", "external-write",
            "policy-1", null, ActionState.Proposed, "stable-idempotency-key", 0,
            T0, null, null, null, null, null, 1, 0);

    private static Task<ActionRecord?> Authorize(
        ActionAuthorizationService service,
        ActionRecord proposedAction,
        ActionAuthorization authorization,
        string? payloadHash = null,
        string? targetScope = null,
        DateTimeOffset? now = null)
        => service.AuthorizeAsync(
            ActionRecord.Create(
                proposedAction.ActionId,
                proposedAction.OwnerPrincipalId,
                proposedAction.CapabilityId,
                proposedAction.CapabilityVersion,
                proposedAction.Intent,
                payloadHash ?? proposedAction.PayloadHash,
                targetScope ?? proposedAction.TargetScope,
                proposedAction.RiskClass,
                proposedAction.PolicyDecisionRef,
                proposedAction.AuthorizationRef,
                proposedAction.State,
                proposedAction.IdempotencyKey,
                proposedAction.AttemptCount,
                proposedAction.CreatedAt,
                proposedAction.StartedAt,
                proposedAction.CompletedAt,
                proposedAction.ProviderReceipt,
                proposedAction.VerificationState,
                proposedAction.Failure,
                proposedAction.SchemaVersion,
                proposedAction.Version),
            authorization.AuthorizationId,
            now ?? T0);

    private sealed class MemoryAuthorizationRepository : IActionAuthorizationRepository
    {
        private readonly Dictionary<string, ActionAuthorization> _entries = new(StringComparer.Ordinal);

        public Task AddAsync(
            string ownerPrincipalId,
            ActionAuthorization authorization,
            CancellationToken cancellationToken = default)
        {
            _entries.Add(Key(ownerPrincipalId, authorization.AuthorizationId), authorization);
            return Task.CompletedTask;
        }

        public Task<ActionAuthorization?> GetAsync(string ownerPrincipalId,string authorizationId,CancellationToken cancellationToken=default)
        { _entries.TryGetValue(Key(ownerPrincipalId,authorizationId),out var value);return Task.FromResult(value); }

        public Task<ActionRecord?> TryConsumeAndAuthorizeAsync(
            string ownerPrincipalId,
            string authorizationId,
            ActionRecord proposedAction,
            DateTimeOffset authorizedAt,
            CancellationToken cancellationToken = default)
        {
            var key = Key(ownerPrincipalId, authorizationId);
            if (!_entries.TryGetValue(key, out var value)
                || value.ConsumedAt is not null
                || authorizedAt < value.IssuedAt
                || authorizedAt >= value.ExpiresAt
                || proposedAction.State != ActionState.Proposed
                || !string.Equals(value.OwnerPrincipalId, proposedAction.OwnerPrincipalId, StringComparison.Ordinal)
                || !string.Equals(value.CapabilityId, proposedAction.CapabilityId, StringComparison.Ordinal)
                || !string.Equals(value.CapabilityVersion, proposedAction.CapabilityVersion, StringComparison.Ordinal)
                || !string.Equals(value.ActionId, proposedAction.ActionId, StringComparison.Ordinal)
                || !string.Equals(value.PayloadHash, proposedAction.PayloadHash, StringComparison.Ordinal)
                || !string.Equals(value.TargetScope, proposedAction.TargetScope, StringComparison.Ordinal))
            {
                return Task.FromResult<ActionRecord?>(null);
            }

            _entries[key] = value with { ConsumedAt = authorizedAt };
            return Task.FromResult<ActionRecord?>(proposedAction.TransitionTo(
                ActionState.Authorized,
                authorizedAt,
                authorizationId));
        }

        private static string Key(string ownerPrincipalId, string authorizationId)
            => $"{ownerPrincipalId}\n{authorizationId}";
    }
}