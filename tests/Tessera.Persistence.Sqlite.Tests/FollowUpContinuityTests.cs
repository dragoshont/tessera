using Tessera.Core.Kernel;
using Xunit;

namespace Tessera.Persistence.Sqlite.Tests;

public sealed class FollowUpContinuityTests
{
    [Fact]
    public void Source_records_require_UTC_and_secret_free_locators()
    {
        var owner = KernelTestData.Principal().PrincipalId;
        var utc = DateTimeOffset.Parse("2026-08-10T09:00:00Z");

        Assert.Throws<ArgumentException>(() => SourceRecord.Create(
            "source-offset",
            owner,
            "local.fixture",
            "native-offset",
            "fixture://r1/offset",
            DateTimeOffset.Parse("2026-08-10T11:00:00+02:00"),
            utc,
            "Safe source content.",
            SensitivityClass.Internal));
        Assert.Throws<ArgumentException>(() => SourceRecord.Create(
            "source-secret-locator",
            owner,
            "local.fixture",
            "native-secret-locator",
            "fixture://r1/item?access_token=secret-value",
            utc,
            utc,
            "Safe source content.",
            SensitivityClass.Internal));
    }

    [Fact]
    public async Task Repository_source_replay_preserves_original_result_version()
    {
        using var database = new TemporaryDatabase();
        var principal = KernelTestData.Principal();
        var store = database.CreateStore();
        await store.InitializeAsync();
        await store.AddAsync(principal);
        var service = new FollowUpContinuityService(
            store,
            new LocalFixtureSourceRecordAdapter(),
            new ManualTimeProvider(DateTimeOffset.Parse("2026-08-10T10:00:00Z")));

        var imported = await service.ImportFixtureAsync(principal.PrincipalId, "initial", "op-initial-version");
        var accepted = await service.AcceptAsync(
            principal.PrincipalId,
            imported.FollowUp.FollowUpId,
            "op-accept-version",
            imported.FollowUp.Version);
        var sourceReceipt = Assert.IsType<FollowUpSourceReceipt>(await store.GetFollowUpSourceAsync(
            principal.PrincipalId,
            "local.fixture",
            "r1-initial"));
        var evidence = Assert.Single(
            await store.ListEvidenceAsync(principal.PrincipalId),
            item => item.SourceNativeId == "r1-initial");
        var observation = Assert.Single(
            await store.ListEventsAsync(principal.PrincipalId),
            item => item.EvidenceRefs.Contains(evidence.EvidenceId, StringComparer.Ordinal));

        var replay = await store.CommitFollowUpAsync(
            principal.PrincipalId,
            new FollowUpCommit(
                accepted.FollowUp,
                accepted.FollowUp.Version,
                "op-concurrent-source-replay",
                "request:concurrent-source-replay",
                new FollowUpSourceIdentity("local.fixture", "r1-initial", sourceReceipt.PayloadHash),
                evidence,
                observation,
                []));

        Assert.True(replay.Replayed);
        Assert.Equal(imported.ResultVersion, replay.ResultVersion);
        Assert.Equal(accepted.FollowUp.Version, replay.FollowUp.Version);
        var operation = Assert.IsType<FollowUpOperationReceipt>(await store.GetFollowUpOperationAsync(
            principal.PrincipalId,
            "op-concurrent-source-replay"));
        Assert.Equal(imported.ResultVersion, operation.ResultVersion);
    }

    [Fact]
    public async Task Candidate_and_conflict_state_with_provenance_survive_restart()
    {
        using var database = new TemporaryDatabase();
        var principal = KernelTestData.Principal();
        var store = database.CreateStore();
        await store.InitializeAsync();
        await store.AddAsync(principal);
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-10T10:00:00Z"));
        var service = new FollowUpContinuityService(store, new LocalFixtureSourceRecordAdapter(), clock);

        var imported = await service.ImportFixtureAsync(principal.PrincipalId, "initial", "restart-initial");
        var accepted = await service.AcceptAsync(
            principal.PrincipalId,
            imported.FollowUp.FollowUpId,
            "restart-accept",
            imported.FollowUp.Version);
        var monday = await service.ImportFixtureAsync(
            principal.PrincipalId,
            "monday",
            "restart-monday",
            accepted.FollowUp.FollowUpId,
            accepted.FollowUp.Version);

        var candidateStore = database.CreateStore();
        await candidateStore.InitializeAsync();
        var candidateService = new FollowUpContinuityService(candidateStore, new LocalFixtureSourceRecordAdapter(), clock);
        var candidateState = Assert.IsType<FollowUp>(await candidateService.GetAsync(
            principal.PrincipalId,
            monday.FollowUp.FollowUpId));
        var dueCandidate = Assert.Single(candidateState.Candidates);
        Assert.Equal("2026-08-17", dueCandidate.Value);
        Assert.Equal(["evidence:local.fixture:r1-monday"], dueCandidate.Provenance.EvidenceRefs);
        Assert.Equal(3, dueCandidate.Provenance.LineageRevisionRefs.Count);

        var mondayAccepted = await candidateService.AcceptAsync(
            principal.PrincipalId,
            candidateState.FollowUpId,
            "restart-accept-monday",
            candidateState.Version);
        var conflicted = await candidateService.ImportFixtureAsync(
            principal.PrincipalId,
            "conflicting-friday",
            "restart-conflict",
            mondayAccepted.FollowUp.FollowUpId,
            mondayAccepted.FollowUp.Version);

        var conflictStore = database.CreateStore();
        await conflictStore.InitializeAsync();
        var conflictState = Assert.IsType<FollowUp>(await conflictStore.GetFollowUpAsync(
            principal.PrincipalId,
            conflicted.FollowUp.FollowUpId));
        Assert.Equal(FollowUpStatus.Conflict, conflictState.Status);
        Assert.Null(conflictState.CurrentField(FollowUpField.DueAt));
        var dueConflicts = conflictState.Revisions
            .Where(revision => revision.Field == FollowUpField.DueAt
                && revision.State == FollowUpRevisionState.Conflicted)
            .ToArray();
        Assert.Equal(2, dueConflicts.Length);
        Assert.Contains(dueConflicts, revision => revision.Provenance.EvidenceRefs.Contains(
            "evidence:local.fixture:r1-conflicting-friday",
            StringComparer.Ordinal));
        Assert.Equal(FollowUpTimelineKind.ConflictDetected, conflictState.Timeline[^1].Kind);
    }

    [Fact]
    public async Task Continuity_scenario_compounds_corrected_context_across_restart()
    {
        using var database = new TemporaryDatabase();
        var principal = KernelTestData.Principal();
        var other = KernelTestData.Principal("tenant-a", "subject-2", "other@example.com");
        var store = database.CreateStore();
        await store.InitializeAsync();
        await store.AddAsync(principal);
        await store.AddAsync(other);
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-10T10:00:00Z"));
        var service = new FollowUpContinuityService(
            store,
            new LocalFixtureSourceRecordAdapter(),
            clock);

        var imported = await service.ImportFixtureAsync(
            principal.PrincipalId,
            "initial",
            "op-initial");
        Assert.Equal(FollowUpStatus.Attention, imported.FollowUp.Status);
        Assert.Equal(3, imported.FollowUp.Candidates.Count);
        Assert.Empty(imported.FollowUp.Current);

        var accepted = await service.AcceptAsync(
            principal.PrincipalId,
            imported.FollowUp.FollowUpId,
            "op-accept-initial",
            imported.FollowUp.Version);
        Assert.Equal(FollowUpStatus.Tracked, accepted.FollowUp.Status);
        Assert.Equal("lease checklist", accepted.FollowUp.CurrentField(FollowUpField.Deliverable)!.Value);
        await Assert.ThrowsAsync<ArgumentException>(() => service.CorrectAsync(
            principal.PrincipalId,
            accepted.FollowUp.FollowUpId,
            "op-secret-correction",
            accepted.FollowUp.Version,
            FollowUpField.Deliverable,
            "password=hunter2"));
        var acceptedReplay = await service.AcceptAsync(
            principal.PrincipalId,
            imported.FollowUp.FollowUpId,
            "op-accept-initial",
            imported.FollowUp.Version);
        Assert.True(acceptedReplay.Replayed);
        Assert.Equal(accepted.ResultVersion, acceptedReplay.ResultVersion);
        await Assert.ThrowsAsync<FollowUpOperationConflictException>(() =>
            service.AcceptAsync(
                principal.PrincipalId,
                imported.FollowUp.FollowUpId,
                "op-accept-initial",
                imported.FollowUp.Version,
                ["different-candidate"]));

        clock.Set(DateTimeOffset.Parse("2026-08-10T11:00:00Z"));
        var corrected = await service.CorrectAsync(
            principal.PrincipalId,
            accepted.FollowUp.FollowUpId,
            "op-correct-deliverable",
            accepted.FollowUp.Version,
            FollowUpField.Deliverable,
            "lease renewal checklist");
        var correctedDeliverable = corrected.FollowUp.CurrentField(FollowUpField.Deliverable)!;
        Assert.Equal("lease renewal checklist", correctedDeliverable.Value);
        Assert.NotNull(correctedDeliverable.Provenance.CorrectionEvidenceRef);
        Assert.Contains(
            corrected.FollowUp.Revisions,
            revision => revision.Field == FollowUpField.Deliverable
                && revision.Value == "lease checklist"
                && revision.State == FollowUpRevisionState.Superseded);

        var monday = await service.ImportFixtureAsync(
            principal.PrincipalId,
            "monday",
            "op-monday",
            corrected.FollowUp.FollowUpId,
            corrected.FollowUp.Version);
        Assert.Equal("2026-08-17", Assert.Single(monday.FollowUp.Candidates).Value);
        Assert.Contains(correctedDeliverable.RevisionId, Assert.Single(monday.FollowUp.Candidates).Provenance.LineageRevisionRefs);

        clock.Set(DateTimeOffset.Parse("2026-08-11T09:30:00Z"));
        var correctedWithCandidate = await service.CorrectAsync(
            principal.PrincipalId,
            monday.FollowUp.FollowUpId,
            "op-correct-with-candidate",
            monday.FollowUp.Version,
            FollowUpField.Counterparty,
            "Rowan");
        Assert.Equal(FollowUpStatus.Attention, correctedWithCandidate.FollowUp.Status);

        clock.Set(DateTimeOffset.Parse("2026-08-11T10:00:00Z"));
        var mondayAccepted = await service.AcceptAsync(
            principal.PrincipalId,
            correctedWithCandidate.FollowUp.FollowUpId,
            "op-accept-monday",
            correctedWithCandidate.FollowUp.Version);
        Assert.Equal("2026-08-17", mondayAccepted.FollowUp.CurrentField(FollowUpField.DueAt)!.Value);

        var restartedStore = database.CreateStore();
        await restartedStore.InitializeAsync();
        var restartedService = new FollowUpContinuityService(
            restartedStore,
            new LocalFixtureSourceRecordAdapter(),
            clock);
        var recovered = await restartedService.GetAsync(
            principal.PrincipalId,
            mondayAccepted.FollowUp.FollowUpId);
        Assert.NotNull(recovered);
        Assert.Equal(mondayAccepted.FollowUp.Version, recovered.Version);
        Assert.Equal(correctedDeliverable.RevisionId, recovered.CurrentField(FollowUpField.Deliverable)!.RevisionId);
        Assert.Equal("2026-08-17", recovered.CurrentField(FollowUpField.DueAt)!.Value);

        var conflicted = await restartedService.ImportFixtureAsync(
            principal.PrincipalId,
            "conflicting-friday",
            "op-conflict-friday",
            recovered.FollowUpId,
            recovered.Version);
        Assert.Equal(FollowUpStatus.Conflict, conflicted.FollowUp.Status);
        Assert.Null(conflicted.FollowUp.CurrentField(FollowUpField.DueAt));
        Assert.Equal(
            ["2026-08-14", "2026-08-17"],
            conflicted.FollowUp.Revisions
                .Where(revision => revision.Field == FollowUpField.DueAt
                    && revision.State == FollowUpRevisionState.Conflicted)
                .Select(revision => revision.Value)
                .Order(StringComparer.Ordinal)
                .ToArray());

        clock.Set(DateTimeOffset.Parse("2026-08-18T09:30:00Z"));
        var correctedWithConflict = await restartedService.CorrectAsync(
            principal.PrincipalId,
            conflicted.FollowUp.FollowUpId,
            "op-correct-with-conflict",
            conflicted.FollowUp.Version,
            FollowUpField.Deliverable,
            "lease renewal checklist");
        Assert.Equal(FollowUpStatus.Conflict, correctedWithConflict.FollowUp.Status);

        clock.Set(DateTimeOffset.Parse("2026-08-18T10:00:00Z"));
        var resolved = await restartedService.ResolveAsync(
            principal.PrincipalId,
            correctedWithConflict.FollowUp.FollowUpId,
            "op-resolve-monday",
            correctedWithConflict.FollowUp.Version,
            FollowUpField.DueAt,
            "2026-08-17");
        var resolvedDueAt = resolved.FollowUp.CurrentField(FollowUpField.DueAt)!;
        Assert.Equal(2, resolvedDueAt.Provenance.LineageRevisionRefs.Count);
        Assert.Equal(FollowUpStatus.Tracked, resolved.FollowUp.Status);

        var staleSource = SourceRecord.Create(
            "stale-friday",
            principal.PrincipalId,
            "local.fixture",
            "r1-stale-friday",
            "fixture://r1/stale-friday",
            DateTimeOffset.Parse("2026-08-09T09:00:00Z"),
            DateTimeOffset.Parse("2026-08-20T09:00:00Z"),
            "The Friday 2026-08-14 deadline still stands.",
            SensitivityClass.Internal);
        var stale = await restartedService.ImportSourceAsync(
            principal.PrincipalId,
            staleSource,
            "op-stale-friday",
            resolved.FollowUp.FollowUpId,
            resolved.FollowUp.Version);
        Assert.Equal(FollowUpStatus.Tracked, stale.FollowUp.Status);
        Assert.Equal("2026-08-17", stale.FollowUp.CurrentField(FollowUpField.DueAt)!.Value);
        Assert.Contains(
            stale.FollowUp.Revisions,
            revision => revision.Value == "2026-08-14"
                && revision.State == FollowUpRevisionState.Rejected);
        Assert.Equal(FollowUpTimelineKind.RejectedStale, stale.FollowUp.Timeline[^1].Kind);

        var sourceReplay = await restartedService.ImportSourceAsync(
            principal.PrincipalId,
            staleSource,
            "op-stale-replay",
            stale.FollowUp.FollowUpId,
            stale.FollowUp.Version);
        Assert.True(sourceReplay.Replayed);
        Assert.Equal(stale.ResultVersion, sourceReplay.ResultVersion);
        Assert.Equal(stale.FollowUp.Version, sourceReplay.FollowUp.Version);
        Assert.Equal(stale.FollowUp.Timeline.Count, sourceReplay.FollowUp.Timeline.Count);

        var staleVersionReplay = await restartedService.ImportSourceAsync(
            principal.PrincipalId,
            staleSource,
            "op-stale-version-replay",
            stale.FollowUp.FollowUpId,
            0);
        Assert.True(staleVersionReplay.Replayed);
        Assert.Equal(stale.ResultVersion, staleVersionReplay.ResultVersion);

        var changedSourcePayload = SourceRecord.Create(
            staleSource.SourceRecordId,
            principal.PrincipalId,
            staleSource.SourceType,
            staleSource.SourceNativeId,
            staleSource.SourceLocator,
            DateTimeOffset.Parse("2026-08-12T09:00:00Z"),
            staleSource.ObservedAt,
            "Monday instead works for it.",
            staleSource.Sensitivity);
        await Assert.ThrowsAsync<FollowUpOperationConflictException>(() =>
            restartedService.ImportSourceAsync(
                principal.PrincipalId,
                changedSourcePayload,
                "op-changed-source-payload",
                stale.FollowUp.FollowUpId,
                stale.FollowUp.Version));

        var collidingSource = SourceRecord.Create(
            "stale-friday-2",
            principal.PrincipalId,
            "local.fixture",
            "r1-stale-friday-2",
            "fixture://r1/stale-friday-2",
            staleSource.OccurredAt,
            staleSource.ObservedAt.AddMinutes(1),
            staleSource.Content,
            staleSource.Sensitivity);
        await Assert.ThrowsAsync<FollowUpOperationConflictException>(() =>
            restartedService.ImportSourceAsync(
                principal.PrincipalId,
                collidingSource,
                "op-stale-replay",
                stale.FollowUp.FollowUpId,
                stale.FollowUp.Version));

        var sent = await restartedService.ImportFixtureAsync(
            principal.PrincipalId,
            "sent",
            "op-sent",
            stale.FollowUp.FollowUpId,
            stale.FollowUp.Version);
        var completionCandidate = Assert.Single(sent.FollowUp.Candidates);
        Assert.Equal(FollowUpField.CompletedAt, completionCandidate.Field);
        Assert.Contains(
            correctedWithConflict.FollowUp.CurrentField(FollowUpField.Deliverable)!.RevisionId,
            completionCandidate.Provenance.LineageRevisionRefs);

        clock.Set(DateTimeOffset.Parse("2026-08-19T10:00:00Z"));
        var completed = await restartedService.AcceptAsync(
            principal.PrincipalId,
            sent.FollowUp.FollowUpId,
            "op-accept-completion",
            sent.FollowUp.Version);
        Assert.Equal(FollowUpStatus.Completed, completed.FollowUp.Status);
        Assert.NotNull(completed.FollowUp.CurrentField(FollowUpField.CompletedAt));
        Assert.Contains(completed.FollowUp.Timeline, entry => entry.Kind == FollowUpTimelineKind.Corrected);
        Assert.Contains(completed.FollowUp.Timeline, entry => entry.Kind == FollowUpTimelineKind.ConflictDetected);
        Assert.Contains(completed.FollowUp.Timeline, entry => entry.Kind == FollowUpTimelineKind.ConflictResolved);

        var evidence = await restartedStore.ListEvidenceAsync(principal.PrincipalId);
        var events = await restartedStore.ListEventsAsync(principal.PrincipalId);
        var dueHistory = await restartedStore.ListHistoryAsync(
            principal.PrincipalId,
            completed.FollowUp.FollowUpId,
            FollowUpField.DueAt.ToString());
        var deliverableHistory = await restartedStore.ListHistoryAsync(
            principal.PrincipalId,
            completed.FollowUp.FollowUpId,
            FollowUpField.Deliverable.ToString());
        Assert.Contains(evidence, item => item.SourceType == "user.correction");
        Assert.Contains(evidence, item => item.SourceType == "user.resolution");
        Assert.Equal(completed.FollowUp.Timeline.Count, events.Count);
        Assert.Contains(dueHistory, assertion => assertion.Value == "2026-08-14"
            && assertion.EpistemicStatus == EpistemicStatus.Rejected);
        Assert.Contains(dueHistory, assertion => assertion.Value == "2026-08-17"
            && assertion.EpistemicStatus == EpistemicStatus.Current);
        var originalDeliverable = Assert.Single(
            deliverableHistory,
            assertion => assertion.Value == "lease checklist");
        Assert.Equal(DateTimeOffset.Parse("2026-08-10T11:00:00Z"), originalDeliverable.SupersededAt);
        Assert.Equal(originalDeliverable.SupersededAt, originalDeliverable.ValidTo);

        Assert.Null(await restartedService.GetAsync(other.PrincipalId, completed.FollowUp.FollowUpId));
        Assert.Empty(await restartedService.ListAsync(other.PrincipalId));
    }

    private sealed class ManualTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset _current = current;

        public override DateTimeOffset GetUtcNow() => _current;

        public void Set(DateTimeOffset current) => _current = current;
    }
}