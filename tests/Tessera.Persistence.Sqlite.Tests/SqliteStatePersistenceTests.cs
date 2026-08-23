using Microsoft.Data.Sqlite;
using Tessera.Core.Kernel;
using Xunit;

namespace Tessera.Persistence.Sqlite.Tests;

public sealed class SqliteStatePersistenceTests
{
    [Fact]
    public async Task Atomic_correction_persists_superseded_before_current_regardless_of_caller_order()
    {
        using var database = new TemporaryDatabase();
        var principal = KernelTestData.Principal();
        var store = database.CreateStore();
        await store.InitializeAsync();
        await store.AddAsync(principal);
        var candidate = KernelTestData.Assertion(
            principal.PrincipalId,
            "assertion-old",
            "15:00",
            AssertionType.Inferred,
            EpistemicStatus.Candidate,
            "evidence-old");
        var oldCurrent = AssertionService.PromoteCandidate(candidate, "accepted");
        await store.SaveBatchAsync(principal.PrincipalId, [oldCurrent]);
        var correction = KernelTestData.Assertion(
            principal.PrincipalId,
            "assertion-new",
            "16:30",
            AssertionType.UserAsserted,
            EpistemicStatus.Candidate,
            "evidence-new",
            KernelTestData.T0.AddMinutes(1));
        var changed = AssertionService.Correct(
            oldCurrent,
            correction,
            KernelTestData.T0.AddMinutes(1),
            "user correction");

        await store.ApplyCorrectionAsync(
            principal.PrincipalId,
            changed.Superseded,
            changed.Current);

        var history = await store.ListHistoryAsync(
            principal.PrincipalId,
            oldCurrent.SubjectKey,
            oldCurrent.Predicate);
        Assert.Contains(history, item => item.EpistemicStatus == EpistemicStatus.Superseded);
        Assert.Equal("16:30", Assert.Single(history, item => item.EpistemicStatus == EpistemicStatus.Current).Value);
    }

    [Fact]
    public async Task Evidence_survives_store_restart()
    {
        using var database = new TemporaryDatabase();
        var principal = KernelTestData.Principal();
        var store = database.CreateStore();
        await store.InitializeAsync();
        await store.AddAsync(principal);
        await store.AddAsync(
            principal.PrincipalId,
            KernelTestData.Evidence(principal.PrincipalId, "evidence-1", "Appointment at 15:00"));

        var restarted = database.CreateStore();
        await restarted.InitializeAsync();
        var loaded = await restarted.GetEvidenceAsync(principal.PrincipalId, "evidence-1");

        Assert.NotNull(loaded);
        Assert.Equal("Appointment at 15:00", loaded.BoundedExcerpt);
        Assert.Equal(ActionPayloadHash.Algorithm, loaded.ContentHashAlgorithm);
        Assert.Equal(ActionPayloadHash.Version, loaded.ContentHashVersion);
    }

    [Fact]
    public async Task Queries_and_writes_are_owner_scoped()
    {
        using var database = new TemporaryDatabase();
        var alice = KernelTestData.Principal(tenant: "tenant-a", subject: "subject-1");
        var bob = KernelTestData.Principal(tenant: "tenant-b", subject: "subject-1");
        var store = database.CreateStore();
        await store.InitializeAsync();
        await store.AddAsync(alice);
        await store.AddAsync(bob);
        var evidence = KernelTestData.Evidence(alice.PrincipalId, "evidence-1", "Alice state");
        await store.AddAsync(alice.PrincipalId, evidence);

        Assert.Null(await store.GetEvidenceAsync(bob.PrincipalId, evidence.EvidenceId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.AddAsync(bob.PrincipalId, evidence));
    }

    [Fact]
    public async Task Correction_preserves_events_and_superseded_assertion_history()
    {
        using var database = new TemporaryDatabase();
        var principal = KernelTestData.Principal();
        var store = database.CreateStore();
        await store.InitializeAsync();
        await store.AddAsync(principal);

        var firstEvidence = KernelTestData.Evidence(principal.PrincipalId, "evidence-1", "Appointment at 15:00");
        var firstEvent = KernelTestData.Event(principal.PrincipalId, "event-1", firstEvidence.EvidenceId);
        var firstCandidate = KernelTestData.Assertion(
            principal.PrincipalId,
            "assertion-1",
            "15:00",
            AssertionType.Inferred,
            EpistemicStatus.Candidate,
            firstEvidence.EvidenceId);
        await store.AddObservationAsync(principal.PrincipalId, firstEvidence, firstEvent, firstCandidate);
        var firstCurrent = AssertionService.PromoteCandidate(firstCandidate, "trusted extraction rule");
        await store.SaveBatchAsync(principal.PrincipalId, [firstCurrent]);

        var correctedAt = KernelTestData.T0.AddHours(1);
        var correctionEvidence = KernelTestData.Evidence(principal.PrincipalId, "evidence-2", "Actually at 16:30");
        var correctionEvent = KernelTestData.Event(
            principal.PrincipalId,
            "event-2",
            correctionEvidence.EvidenceId,
            "calendar.corrected",
            correctedAt);
        var correctionCandidate = KernelTestData.Assertion(
            principal.PrincipalId,
            "assertion-2",
            "16:30",
            AssertionType.UserAsserted,
            EpistemicStatus.Candidate,
            correctionEvidence.EvidenceId,
            correctedAt);
        await store.AddObservationAsync(
            principal.PrincipalId,
            correctionEvidence,
            correctionEvent,
            correctionCandidate);
        var corrected = AssertionService.Correct(
            firstCurrent,
            correctionCandidate,
            correctedAt,
            "explicit user correction");
        await store.SaveBatchAsync(principal.PrincipalId, [corrected.Superseded, corrected.Current]);

        var events = await store.ListEventsAsync(principal.PrincipalId);
        var current = Assert.Single(await store.ListCurrentAsync(principal.PrincipalId));
        var history = await store.ListHistoryAsync(principal.PrincipalId, "appointment-1", "start-time");
        Assert.Equal(2, events.Count);
        Assert.Equal("16:30", current.Value);
        Assert.Equal(AssertionType.UserAsserted, current.AssertionType);
        Assert.Equal(2, history.Count);
        Assert.Contains(history, assertion => assertion.EpistemicStatus == EpistemicStatus.Superseded
            && assertion.Value == "15:00"
            && assertion.EvidenceRefs.Contains("evidence-1", StringComparer.Ordinal));
    }

    [Fact]
    public async Task Conflict_is_persisted_without_a_last_write_winner()
    {
        using var database = new TemporaryDatabase();
        var principal = KernelTestData.Principal();
        var store = database.CreateStore();
        await store.InitializeAsync();
        await store.AddAsync(principal);
        var first = KernelTestData.Assertion(
            principal.PrincipalId,
            "assertion-1",
            "15:00",
            AssertionType.SourceAsserted,
            EpistemicStatus.Supported,
            "evidence-1");
        var second = KernelTestData.Assertion(
            principal.PrincipalId,
            "assertion-2",
            "16:30",
            AssertionType.SourceAsserted,
            EpistemicStatus.Supported,
            "evidence-2");
        var conflict = AssertionService.MarkConflict(first, second, "credible sources disagree");

        await store.SaveBatchAsync(principal.PrincipalId, [conflict.First, conflict.Second]);

        Assert.Empty(await store.ListCurrentAsync(principal.PrincipalId));
        var history = await store.ListHistoryAsync(principal.PrincipalId, "appointment-1", "start-time");
        Assert.Equal(2, history.Count);
        Assert.All(history, assertion => Assert.Equal(EpistemicStatus.Conflicted, assertion.EpistemicStatus));
    }

    [Fact]
    public async Task Trusted_state_projects_corrected_history_and_conflicts_from_existing_schema()
    {
        using var database = new TemporaryDatabase();
        var principal = KernelTestData.Principal();
        var store = database.CreateStore();
        await store.InitializeAsync();
        await store.AddAsync(principal);
        var oldEvidence = KernelTestData.Evidence(principal.PrincipalId, "evidence-old", "Appointment at 15:00");
        var newEvidence = KernelTestData.Evidence(principal.PrincipalId, "evidence-new", "Appointment at 16:30");
        var locationA = KernelTestData.Evidence(principal.PrincipalId, "evidence-location-a", "Location A");
        var locationB = KernelTestData.Evidence(principal.PrincipalId, "evidence-location-b", "Location B");
        foreach (var evidence in new[] { oldEvidence, newEvidence, locationA, locationB })
        {
            await store.AddAsync(principal.PrincipalId, evidence);
        }

        var oldCurrent = AssertionService.PromoteCandidate(
            KernelTestData.Assertion(
                principal.PrincipalId,
                "assertion-old",
                "15:00",
                AssertionType.Inferred,
                EpistemicStatus.Candidate,
                oldEvidence.EvidenceId),
            "accepted");
        await store.SaveBatchAsync(principal.PrincipalId, [oldCurrent]);
        var correction = KernelTestData.Assertion(
            principal.PrincipalId,
            "assertion-new",
            "16:30",
            AssertionType.UserAsserted,
            EpistemicStatus.Candidate,
            newEvidence.EvidenceId,
            KernelTestData.T0.AddMinutes(1));
        var corrected = AssertionService.Correct(
            oldCurrent,
            correction,
            KernelTestData.T0.AddMinutes(1),
            "user correction");
        await store.ApplyCorrectionAsync(principal.PrincipalId, corrected.Superseded, corrected.Current);
        var conflict = AssertionService.MarkConflict(
            AssertionRecord.Create(
                "location-a", principal.PrincipalId, "appointment-1", "location", "A",
                AssertionType.SourceAsserted, EpistemicStatus.Supported, 0.9m,
                KernelTestData.T0, null, KernelTestData.T0, null,
                [locationA.EvidenceId], [], null, KernelTestData.Producer, 1),
            AssertionRecord.Create(
                "location-b", principal.PrincipalId, "appointment-1", "location", "B",
                AssertionType.SourceAsserted, EpistemicStatus.Supported, 0.9m,
                KernelTestData.T0, null, KernelTestData.T0, null,
                [locationB.EvidenceId], [], null, KernelTestData.Producer, 1),
            "sources disagree");
        await store.SaveBatchAsync(principal.PrincipalId, [conflict.First, conflict.Second]);

        var snapshot = await new TrustedStateProjection(store, store).ProjectAsync(
            TrustedStateQuery.Create(
                principal.PrincipalId,
                [
                    TrustedStateKey.Create("appointment-1", "start-time"),
                    TrustedStateKey.Create("appointment-1", "location"),
                ]));

        var time = Assert.Single(snapshot.Entries, entry => entry.Key.Predicate == "start-time");
        Assert.Equal("16:30", time.Current?.Value);
        Assert.Equal("15:00", Assert.Single(time.History).Value);
        var location = Assert.Single(snapshot.Entries, entry => entry.Key.Predicate == "location");
        Assert.Null(location.Current);
        Assert.Equal(2, location.Conflicts.Count);
    }

    [Fact]
    public async Task Store_rejects_two_current_values_for_one_owner_and_key()
    {
        using var database = new TemporaryDatabase();
        var principal = KernelTestData.Principal();
        var store = database.CreateStore();
        await store.InitializeAsync();
        await store.AddAsync(principal);
        var first = AssertionService.PromoteCandidate(
            KernelTestData.Assertion(
                principal.PrincipalId,
                "assertion-1",
                "15:00",
                AssertionType.SourceAsserted,
                EpistemicStatus.Candidate,
                "evidence-1"),
            "trusted source rule");
        var second = AssertionService.PromoteCandidate(
            KernelTestData.Assertion(
                principal.PrincipalId,
                "assertion-2",
                "16:30",
                AssertionType.SourceAsserted,
                EpistemicStatus.Candidate,
                "evidence-2"),
            "trusted source rule");

        await Assert.ThrowsAsync<SqliteException>(() => store.SaveBatchAsync(
            principal.PrincipalId,
            [first, second]));
        Assert.Empty(await store.ListCurrentAsync(principal.PrincipalId));
    }

    [Fact]
    public async Task Retention_state_changes_without_erasing_evidence_identity()
    {
        using var database = new TemporaryDatabase();
        var principal = KernelTestData.Principal();
        var store = database.CreateStore();
        await store.InitializeAsync();
        await store.AddAsync(principal);
        var evidence = KernelTestData.Evidence(principal.PrincipalId, "evidence-1", "Sensitive excerpt");
        await store.AddAsync(principal.PrincipalId, evidence);

        Assert.True(await store.UpdateRetentionAsync(
            principal.PrincipalId,
            evidence.EvidenceId,
            RetentionState.Deleted));

        var retained = await store.GetEvidenceAsync(principal.PrincipalId, evidence.EvidenceId);
        Assert.NotNull(retained);
        Assert.Equal(RetentionState.Deleted, retained.RetentionState);
    }

    [Fact]
    public async Task Failed_observation_transaction_leaves_no_partial_evidence_or_assertion()
    {
        using var database = new TemporaryDatabase();
        var principal = KernelTestData.Principal();
        var store = database.CreateStore();
        await store.InitializeAsync();
        await store.AddAsync(principal);
        var duplicateEvent = KernelTestData.Event(principal.PrincipalId, "event-duplicate", "existing-evidence");
        await store.AppendAsync(principal.PrincipalId, duplicateEvent);
        var evidence = KernelTestData.Evidence(principal.PrincipalId, "evidence-new", "new data");
        var assertion = KernelTestData.Assertion(
            principal.PrincipalId,
            "assertion-new",
            "new",
            AssertionType.Extracted,
            EpistemicStatus.Candidate,
            evidence.EvidenceId);

        await Assert.ThrowsAsync<SqliteException>(() => store.AddObservationAsync(
            principal.PrincipalId,
            evidence,
            duplicateEvent,
            assertion));

        Assert.Null(await store.GetEvidenceAsync(principal.PrincipalId, evidence.EvidenceId));
        Assert.Null(await store.GetAssertionAsync(principal.PrincipalId, assertion.AssertionId));
    }
}