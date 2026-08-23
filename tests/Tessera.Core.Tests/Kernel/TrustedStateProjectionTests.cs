using Tessera.Core.Audit;
using Tessera.Core.Broker;
using Tessera.Core.Identity;
using Tessera.Core.Kernel;
using Tessera.Core.Model;
using Tessera.Core.Policy;
using Tessera.Core.Resolution;
using Xunit;

namespace Tessera.Core.Tests.Kernel;

public sealed class TrustedStateProjectionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 23, 9, 0, 0, TimeSpan.Zero);
    private static readonly ProducerRef Producer = ProducerRef.Create("trusted-state-test", "1");

    [Fact]
    public async Task Projection_preserves_current_history_conflicts_and_evidence_deterministically()
    {
        var oldCurrent = Assertion("old", "appointment", "time", "15:00", EpistemicStatus.Current, "e-old");
        var correction = Assertion("new", "appointment", "time", "16:30", EpistemicStatus.Candidate, "e-new", AssertionType.UserAsserted);
        var corrected = AssertionService.Correct(oldCurrent, correction, T0.AddHours(1), "user correction");
        var firstConflict = Assertion("conflict-b", "appointment", "location", "Baneasa", EpistemicStatus.Conflicted, "e-b", at: T0.AddMinutes(2));
        var secondConflict = Assertion("conflict-a", "appointment", "location", "Victoriei", EpistemicStatus.Conflicted, "e-a", at: T0.AddMinutes(1));
        var assertions = new MemoryAssertionRepository(
            [corrected.Current, corrected.Superseded],
            [firstConflict, secondConflict]);
        var evidence = new MemoryEvidenceRepository(
            Evidence("e-old", SensitivityClass.Internal),
            Evidence("e-new", SensitivityClass.Internal),
            Evidence("e-a", SensitivityClass.Confidential),
            Evidence("e-b", SensitivityClass.Confidential));
        var projection = new TrustedStateProjection(assertions, evidence);
        var query = TrustedStateQuery.Create(
            Owner,
            [
                TrustedStateKey.Create("appointment", "location"),
                TrustedStateKey.Create("appointment", "time"),
            ],
            10);

        var snapshot = await projection.ProjectAsync(query);

        Assert.False(snapshot.IsTruncated);
        Assert.Equal(["location", "time"], snapshot.Entries.Select(entry => entry.Key.Predicate));
        var location = snapshot.Entries[0];
        Assert.Null(location.Current);
        Assert.Equal(["conflict-b", "conflict-a"], location.Conflicts.Select(item => item.AssertionId));
        Assert.Equal(["e-a", "e-b"], location.Evidence.Select(item => item.EvidenceId));
        var time = snapshot.Entries[1];
        Assert.Equal("16:30", time.Current?.Value);
        Assert.Equal("15:00", Assert.Single(time.History).Value);
        Assert.Contains("old", time.Current!.LineageRefs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Context_release_denies_or_steps_up_before_repository_access(bool stepUp)
    {
        var user = User();
        var owner = Assert.IsType<string>(user.CanonicalPrincipalId);
        var action = "read:context";
        var grant = new Grant(
            "worker",
            $"context:{owner}",
            [action],
            owner,
            stepUp ? [action] : null);
        var audit = new CapturingAuditSink();
        var service = new ContextReleaseService(
            new PolicyDecisionPoint(stepUp ? [grant] : []),
            new TrustedStateProjection(new ThrowingAssertionRepository(), new ThrowingEvidenceRepository()),
            audit);

        var result = await service.ReleaseAsync(Request(user, owner));

        Assert.Equal(stepUp ? Effect.StepUp : Effect.Deny, result.Decision.Effect);
        Assert.Null(result.Envelope);
        Assert.Single(audit.Entries);
    }

    [Fact]
    public async Task Context_release_is_owner_bound_and_filters_restricted_state()
    {
        var user = User();
        var owner = Assert.IsType<string>(user.CanonicalPrincipalId);
        var current = Assertion("current", "appointment", "time", "16:30", EpistemicStatus.Current, "e-current", owner: owner);
        var conflict = Assertion("conflict", "appointment", "location", "Victoriei", EpistemicStatus.Conflicted, "e-conflict", owner: owner);
        var projection = new TrustedStateProjection(
            new MemoryAssertionRepository([current], [conflict]),
            new MemoryEvidenceRepository(
                Evidence("e-current", SensitivityClass.Internal, owner),
                Evidence("e-conflict", SensitivityClass.Restricted, owner)));
        var service = new ContextReleaseService(
            new PolicyDecisionPoint([
                new Grant("worker", $"context:{owner}", ["read:context"], owner),
            ]),
            projection,
            new CapturingAuditSink());

        var result = await service.ReleaseAsync(Request(user, owner));

        Assert.True(result.Decision.Allowed);
        var envelope = Assert.IsType<ContextEnvelope>(result.Envelope);
        Assert.Equal("appointment.time = 16:30", Assert.Single(envelope.CurrentFacts).Content);
        Assert.Empty(envelope.UncertainAssertions);
        Assert.Contains(envelope.Omissions, omission => omission.ItemId == "conflict");
        Assert.Equal("release appointment context", result.DisclosureReason);
    }

    [Fact]
    public async Task Context_release_rejects_owner_mismatch_before_repository_access()
    {
        var user = User();
        var owner = Assert.IsType<string>(user.CanonicalPrincipalId);
        var foreignOwner = "principal:sha256:foreign";
        var audit = new CapturingAuditSink();
        var service = new ContextReleaseService(
            new PolicyDecisionPoint([
                new Grant("worker", $"context:{foreignOwner}", ["read:context"], owner),
            ]),
            new TrustedStateProjection(new ThrowingAssertionRepository(), new ThrowingEvidenceRepository()),
            audit);

        var result = await service.ReleaseAsync(Request(user, foreignOwner));

        Assert.Equal(Effect.Deny, result.Decision.Effect);
        Assert.Null(result.Envelope);
        Assert.Single(audit.Entries);
    }

    [Fact]
    public async Task Projection_prioritizes_current_state_and_reports_truncation()
    {
        var current = Assertion("current", "appointment", "time", "16:30", EpistemicStatus.Current, "e-current");
        var history = Assertion("history", "appointment", "time", "15:00", EpistemicStatus.Superseded, "e-history");
        var projection = new TrustedStateProjection(
            new MemoryAssertionRepository([history, current]),
            new MemoryEvidenceRepository(
                Evidence("e-current", SensitivityClass.Internal),
                Evidence("e-history", SensitivityClass.Internal)));

        var snapshot = await projection.ProjectAsync(TrustedStateQuery.Create(
            Owner,
            [TrustedStateKey.Create("appointment", "time")],
            1));

        Assert.True(snapshot.IsTruncated);
        var entry = Assert.Single(snapshot.Entries);
        Assert.Equal("current", entry.Current?.AssertionId);
        Assert.Empty(entry.History);
    }

    [Fact]
    public async Task Projection_rejects_foreign_evidence_returned_by_repository()
    {
        var assertion = Assertion("current", "appointment", "time", "16:30", EpistemicStatus.Current, "evidence");
        var projection = new TrustedStateProjection(
            new MemoryAssertionRepository([assertion]),
            new ForeignEvidenceRepository(Evidence("evidence", SensitivityClass.Internal, "principal:sha256:foreign")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => projection.ProjectAsync(
            TrustedStateQuery.Create(Owner, [TrustedStateKey.Create("appointment", "time")])));
    }

    [Fact]
    public async Task Projection_rejects_foreign_assertion_before_loading_evidence()
    {
        var foreign = Assertion(
            "foreign",
            "appointment",
            "time",
            "16:30",
            EpistemicStatus.Current,
            "evidence",
            owner: "principal:sha256:foreign");
        var projection = new TrustedStateProjection(
            new ForeignAssertionRepository(foreign),
            new ThrowingEvidenceRepository());

        await Assert.ThrowsAsync<InvalidOperationException>(() => projection.ProjectAsync(
            TrustedStateQuery.Create(Owner, [TrustedStateKey.Create("appointment", "time")])));
    }

    [Fact]
    public async Task Projection_rejects_duplicate_current_assertions()
    {
        var projection = new TrustedStateProjection(
            new MemoryAssertionRepository([
                Assertion("first", "appointment", "time", "15:00", EpistemicStatus.Current, "e-first"),
                Assertion("second", "appointment", "time", "16:30", EpistemicStatus.Current, "e-second"),
            ]),
            new ThrowingEvidenceRepository());

        await Assert.ThrowsAsync<InvalidOperationException>(() => projection.ProjectAsync(
            TrustedStateQuery.Create(Owner, [TrustedStateKey.Create("appointment", "time")])));
    }

    [Fact]
    public async Task Projection_excludes_unaccepted_candidates_and_supported_assertions()
    {
        var projection = new TrustedStateProjection(
            new MemoryAssertionRepository([
                Assertion("candidate", "appointment", "time", "15:00", EpistemicStatus.Candidate, "e-candidate"),
                Assertion("supported", "appointment", "time", "16:00", EpistemicStatus.Supported, "e-supported"),
                Assertion("current", "appointment", "time", "16:30", EpistemicStatus.Current, "e-current"),
            ]),
            new MemoryEvidenceRepository(Evidence("e-current", SensitivityClass.Internal)));

        var snapshot = await projection.ProjectAsync(
            TrustedStateQuery.Create(Owner, [TrustedStateKey.Create("appointment", "time")]));

        var entry = Assert.Single(snapshot.Entries);
        Assert.Equal("current", entry.Current?.AssertionId);
        Assert.Empty(entry.History);
        Assert.Empty(entry.Conflicts);
        Assert.Equal("e-current", Assert.Single(entry.Evidence).EvidenceId);
    }

    [Fact]
    public void Query_rejects_more_than_one_hundred_keys()
    {
        var keys = Enumerable.Range(0, 101)
            .Select(index => TrustedStateKey.Create($"subject-{index}", "value"));

        Assert.Throws<ArgumentException>(() => TrustedStateQuery.Create(Owner, keys));
    }

    private const string Owner = "principal:sha256:owner";

    private static ContextReleaseRequest Request(EndUserAssertion user, string owner)
        => new(
            new CallerIdentity("worker", VerificationMethod.Network),
            user,
            TrustedStateQuery.Create(
                owner,
                [
                    TrustedStateKey.Create("appointment", "time"),
                    TrustedStateKey.Create("appointment", "location"),
                ],
                10),
            new ContextBuildRequest(
                owner,
                "answer appointment question",
                "task-1",
                1024,
                new HashSet<SensitivityClass> { SensitivityClass.Internal },
                []),
            "release appointment context");

    private static EndUserAssertion User()
        => new("user-1", "https://issuer.example", VerificationMethod.OidcJwt, "user@example.com", "tenant-1");

    private static EvidenceRecord Evidence(
        string id,
        SensitivityClass sensitivity,
        string owner = Owner)
        => EvidenceRecord.Create(
            id, owner, "test", id, $"test://{id}", T0, T0, "SHA-256", 1,
            new string('a', 64), RetentionState.Active, sensitivity, Producer, 1, "bounded");

    private static AssertionRecord Assertion(
        string id,
        string subject,
        string predicate,
        string value,
        EpistemicStatus status,
        string evidenceId,
        AssertionType type = AssertionType.SourceAsserted,
        string owner = Owner,
        DateTimeOffset? at = null)
        => AssertionRecord.Create(
            id, owner, subject, predicate, value, type, status, 0.9m, at ?? T0, null,
            at ?? T0, null, [evidenceId], [], null, Producer, 1);

    private sealed class MemoryAssertionRepository(params IReadOnlyList<AssertionRecord>[] groups) : IAssertionRepository
    {
        private readonly IReadOnlyList<AssertionRecord> _items = groups.SelectMany(group => group).ToArray();

        public Task<IReadOnlyList<AssertionRecord>> ListHistoryAsync(string ownerPrincipalId, string subjectKey, string predicate, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AssertionRecord>>(_items.Where(item =>
                item.OwnerPrincipalId == ownerPrincipalId && item.SubjectKey == subjectKey && item.Predicate == predicate).Reverse().ToArray());

        public Task SaveBatchAsync(string ownerPrincipalId, IReadOnlyCollection<AssertionRecord> assertions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AssertionRecord?> GetAsync(string ownerPrincipalId, string assertionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AssertionRecord>> ListCurrentAsync(string ownerPrincipalId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ApplyCorrectionAsync(string ownerPrincipalId, AssertionRecord superseded, AssertionRecord current, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class MemoryEvidenceRepository(params EvidenceRecord[] evidence) : IEvidenceRepository
    {
        private readonly Dictionary<string, EvidenceRecord> _items = evidence.ToDictionary(item => item.EvidenceId, StringComparer.Ordinal);

        public Task<EvidenceRecord?> GetAsync(string ownerPrincipalId, string evidenceId, CancellationToken cancellationToken = default)
            => Task.FromResult(_items.TryGetValue(evidenceId, out var item) && item.OwnerPrincipalId == ownerPrincipalId ? item : null);

        public Task AddAsync(string ownerPrincipalId, EvidenceRecord evidence, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<EvidenceRecord>> ListAsync(string ownerPrincipalId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> UpdateRetentionAsync(string ownerPrincipalId, string evidenceId, RetentionState retentionState, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ThrowingAssertionRepository : IAssertionRepository
    {
        public Task<IReadOnlyList<AssertionRecord>> ListHistoryAsync(string ownerPrincipalId, string subjectKey, string predicate, CancellationToken cancellationToken = default) => throw new Xunit.Sdk.XunitException("Repository read before authorization.");
        public Task SaveBatchAsync(string ownerPrincipalId, IReadOnlyCollection<AssertionRecord> assertions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AssertionRecord?> GetAsync(string ownerPrincipalId, string assertionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AssertionRecord>> ListCurrentAsync(string ownerPrincipalId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ApplyCorrectionAsync(string ownerPrincipalId, AssertionRecord superseded, AssertionRecord current, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ForeignAssertionRepository(AssertionRecord assertion) : IAssertionRepository
    {
        public Task<IReadOnlyList<AssertionRecord>> ListHistoryAsync(string ownerPrincipalId, string subjectKey, string predicate, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AssertionRecord>>([assertion]);

        public Task SaveBatchAsync(string ownerPrincipalId, IReadOnlyCollection<AssertionRecord> assertions, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AssertionRecord?> GetAsync(string ownerPrincipalId, string assertionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AssertionRecord>> ListCurrentAsync(string ownerPrincipalId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ApplyCorrectionAsync(string ownerPrincipalId, AssertionRecord superseded, AssertionRecord current, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ThrowingEvidenceRepository : IEvidenceRepository
    {
        public Task<EvidenceRecord?> GetAsync(string ownerPrincipalId, string evidenceId, CancellationToken cancellationToken = default) => throw new Xunit.Sdk.XunitException("Repository read before authorization.");
        public Task AddAsync(string ownerPrincipalId, EvidenceRecord evidence, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<EvidenceRecord>> ListAsync(string ownerPrincipalId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> UpdateRetentionAsync(string ownerPrincipalId, string evidenceId, RetentionState retentionState, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ForeignEvidenceRepository(EvidenceRecord evidence) : IEvidenceRepository
    {
        public Task<EvidenceRecord?> GetAsync(string ownerPrincipalId, string evidenceId, CancellationToken cancellationToken = default)
            => Task.FromResult<EvidenceRecord?>(evidence);

        public Task AddAsync(string ownerPrincipalId, EvidenceRecord item, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<EvidenceRecord>> ListAsync(string ownerPrincipalId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> UpdateRetentionAsync(string ownerPrincipalId, string evidenceId, RetentionState retentionState, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class CapturingAuditSink : IAuditSink
    {
        public List<AuditEntry> Entries { get; } = [];

        public void Record(AccessRequest request, Decision decision, ResolvedCredential? credential)
            => Entries.Add(AuditEntry.From(request, decision, credential));
    }
}