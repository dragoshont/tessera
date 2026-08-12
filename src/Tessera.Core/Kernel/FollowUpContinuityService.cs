using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Tessera.Core.Kernel;

public sealed class FollowUpContinuityService(
    IFollowUpRepository repository,
    ISourceRecordAdapter sourceAdapter,
    TimeProvider? timeProvider = null)
{
    private static readonly ProducerRef Parser = ProducerRef.Create(
        "tessera.followup.fixture",
        DeterministicFollowUpExtractor.ParserVersion);
    private static readonly ProducerRef UserDecision = ProducerRef.Create(
        "tessera.followup.user-decision",
        "1");
    private readonly IFollowUpRepository _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly ISourceRecordAdapter _sourceAdapter = sourceAdapter ?? throw new ArgumentNullException(nameof(sourceAdapter));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<FollowUp?> GetAsync(
        string ownerPrincipalId,
        string followUpId,
        CancellationToken cancellationToken = default)
        => _repository.GetFollowUpAsync(ownerPrincipalId, followUpId, cancellationToken);

    public Task<IReadOnlyList<FollowUp>> ListAsync(
        string ownerPrincipalId,
        FollowUpStatus? status = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
        => _repository.ListFollowUpsAsync(ownerPrincipalId, status, limit, cancellationToken);

    public async Task<FollowUpCommitResult> ImportFixtureAsync(
        string ownerPrincipalId,
        string fixtureId,
        string operationId,
        string? followUpId = null,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        var source = _sourceAdapter.Read(ownerPrincipalId, fixtureId)
            ?? throw new KeyNotFoundException("Unknown R1 fixture.");
        return await ImportSourceAsync(
            ownerPrincipalId,
            source,
            operationId,
            followUpId,
            expectedVersion,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<FollowUpCommitResult> ImportSourceAsync(
        string ownerPrincipalId,
        SourceRecord source,
        string operationId,
        string? followUpId = null,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        EnsureOwner(ownerPrincipalId, source.OwnerPrincipalId);
        var operation = ValidateOperationId(operationId);
        var initial = source.SourceNativeId == "r1-initial";
        if (initial && followUpId is not null)
        {
            throw new ArgumentException("The initial fixture creates its own FollowUp.", nameof(followUpId));
        }

        if (!initial && (string.IsNullOrWhiteSpace(followUpId) || expectedVersion is null))
        {
            throw new ArgumentException("Contextual fixtures require an exact FollowUp ID and version.", nameof(followUpId));
        }

        var targetId = initial ? "followup:r1-lease-rowan" : ValidateFollowUpId(followUpId!);
        var sourcePayloadHash = HashRequest(
            source.SourceRecordId,
            source.OwnerPrincipalId,
            source.SourceType,
            source.SourceNativeId,
            source.SourceLocator,
            source.OccurredAt.ToString("O", CultureInfo.InvariantCulture),
            source.ObservedAt.ToString("O", CultureInfo.InvariantCulture),
            source.Content,
            source.Sensitivity.ToString());
        var requestHash = HashRequest(
            "import",
            source.SourceType,
            source.SourceNativeId,
            sourcePayloadHash,
            targetId,
            expectedVersion?.ToString(CultureInfo.InvariantCulture) ?? "new");
        var replay = await ReplayAsync(
            ownerPrincipalId,
            operation,
            requestHash,
            cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            return replay;
        }

        var sourceReplay = await _repository.GetFollowUpSourceAsync(
            ownerPrincipalId,
            source.SourceType,
            source.SourceNativeId,
            cancellationToken).ConfigureAwait(false);
        if (sourceReplay is not null)
        {
            if (string.IsNullOrEmpty(sourceReplay.PayloadHash)
                || !string.Equals(sourceReplay.PayloadHash, sourcePayloadHash, StringComparison.Ordinal))
            {
                throw new FollowUpOperationConflictException("Source identity was already used with a different or unverifiable payload.");
            }

            var replayedFollowUp = await _repository.GetFollowUpAsync(
                ownerPrincipalId,
                sourceReplay.FollowUpId,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Source receipt references a missing FollowUp.");
            await _repository.RecordFollowUpOperationAsync(
                ownerPrincipalId,
                operation,
                requestHash,
                sourceReplay.FollowUpId,
                sourceReplay.ResultVersion,
                cancellationToken).ConfigureAwait(false);
            return new FollowUpCommitResult(replayedFollowUp, true, sourceReplay.ResultVersion);
        }

        var current = initial
            ? null
            : await _repository.GetFollowUpAsync(ownerPrincipalId, targetId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("FollowUp not found.");
        if (current is not null && current.Version != expectedVersion)
        {
            throw new FollowUpConcurrencyException("FollowUp version is stale.");
        }

        var extraction = DeterministicFollowUpExtractor.Extract(source, current);
        if (extraction.Status == FollowUpExtractionStatus.NeedsContext)
        {
            throw new FollowUpNeedsContextException("The fixture cannot be resolved from accepted current context.");
        }

        if (extraction.Status == FollowUpExtractionStatus.Unsupported)
        {
            throw new NotSupportedException("The deterministic FollowUp parser does not support this source.");
        }

        var evidence = SourceEvidence(source);
        var next = ApplyExtraction(current, targetId, ownerPrincipalId, source, evidence, extraction);
        return await CommitAsync(
            ownerPrincipalId,
            next,
            current?.Version,
            operation,
            requestHash,
            new FollowUpSourceIdentity(source.SourceType, source.SourceNativeId, sourcePayloadHash),
            evidence,
            source.OccurredAt,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<FollowUpCommitResult> AcceptAsync(
        string ownerPrincipalId,
        string followUpId,
        string operationId,
        long expectedVersion,
        IReadOnlyCollection<string>? candidateRevisionIds = null,
        CancellationToken cancellationToken = default)
    {
        var operation = ValidateOperationId(operationId);
        var requestedCandidates = candidateRevisionIds is null or { Count: 0 }
            ? "all"
            : string.Join(',', candidateRevisionIds.Order(StringComparer.Ordinal));
        var requestHash = HashRequest(
            "accept",
            followUpId,
            expectedVersion.ToString(CultureInfo.InvariantCulture),
            requestedCandidates);
        var replay = await ReplayAsync(
            ownerPrincipalId,
            operation,
            requestHash,
            cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            return replay;
        }

        var current = await RequiredCurrentAsync(
            ownerPrincipalId,
            followUpId,
            expectedVersion,
            cancellationToken).ConfigureAwait(false);
        var selected = candidateRevisionIds is null or { Count: 0 }
            ? current.Candidates.Select(revision => revision.RevisionId).ToHashSet(StringComparer.Ordinal)
            : candidateRevisionIds.Select(id => KernelValidation.Text(id, nameof(candidateRevisionIds), 256))
                .ToHashSet(StringComparer.Ordinal);
        if (selected.Count == 0
            || selected.Any(id => current.Candidates.All(candidate => candidate.RevisionId != id)))
        {
            throw new InvalidOperationException("Acceptance must select existing candidate revisions.");
        }

        var now = DecisionTime(current);
        var evidence = UserEvidence(ownerPrincipalId, operationId, "user.acceptance", "Accepted FollowUp candidate revisions.", now);
        var revisions = current.Revisions.ToList();
        foreach (var candidate in current.Candidates.Where(candidate => selected.Contains(candidate.RevisionId)))
        {
            for (var index = 0; index < revisions.Count; index++)
            {
                var revision = revisions[index];
                if (revision.Field == candidate.Field
                    && revision.State == FollowUpRevisionState.Current)
                {
                    revisions[index] = revision.WithState(FollowUpRevisionState.Superseded);
                }
            }

            var candidateIndex = revisions.FindIndex(revision => revision.RevisionId == candidate.RevisionId);
            var acceptedProvenance = FollowUpFieldProvenance.Create(
                candidate.Provenance.EvidenceRefs.Append(evidence.EvidenceId),
                candidate.Provenance.SourceTimestamp,
                candidate.Provenance.ParserVersion,
                candidate.Provenance.Confidence,
                candidate.Provenance.CorrectionEvidenceRef,
                candidate.Provenance.LineageRevisionRefs);
            revisions[candidateIndex] = candidate.WithState(FollowUpRevisionState.Current, acceptedProvenance);
        }

        var status = DeriveStatus(revisions);
        var kind = status == FollowUpStatus.Completed
            ? FollowUpTimelineKind.Completed
            : FollowUpTimelineKind.Accepted;
        var next = Next(
            current,
            status,
            revisions,
            Timeline(current, kind, null, "Accepted candidate revisions.", evidence, now));
        return await CommitAsync(
            ownerPrincipalId,
            next,
            expectedVersion,
            operation,
            requestHash,
            null,
            evidence,
            now,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<FollowUpCommitResult> CorrectAsync(
        string ownerPrincipalId,
        string followUpId,
        string operationId,
        long expectedVersion,
        FollowUpField field,
        string value,
        CancellationToken cancellationToken = default)
    {
        var operation = ValidateOperationId(operationId);
        var requestHash = HashRequest(
            "correct",
            followUpId,
            expectedVersion.ToString(CultureInfo.InvariantCulture),
            field.ToString(),
            value);
        var replay = await ReplayAsync(
            ownerPrincipalId,
            operation,
            requestHash,
            cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            return replay;
        }

        var current = await RequiredCurrentAsync(
            ownerPrincipalId,
            followUpId,
            expectedVersion,
            cancellationToken).ConfigureAwait(false);
        var prior = current.CurrentField(field)
            ?? throw new InvalidOperationException("Only a current FollowUp field can be corrected.");
        var now = DecisionTime(current);
        var evidence = UserEvidence(ownerPrincipalId, operationId, "user.correction", $"Corrected FollowUp {field}.", now);
        var revisions = current.Revisions
            .Select(revision => revision.RevisionId == prior.RevisionId
                ? revision.WithState(FollowUpRevisionState.Superseded)
                : revision)
            .ToList();
        revisions.Add(UserRevision(operationId, field, value, evidence, now, [prior.RevisionId]));
        var next = Next(
            current,
            DeriveStatus(revisions),
            revisions,
            Timeline(current, FollowUpTimelineKind.Corrected, field, $"Corrected {field}.", evidence, now));
        return await CommitAsync(
            ownerPrincipalId,
            next,
            expectedVersion,
            operation,
            requestHash,
            null,
            evidence,
            now,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<FollowUpCommitResult> ResolveAsync(
        string ownerPrincipalId,
        string followUpId,
        string operationId,
        long expectedVersion,
        FollowUpField field,
        string value,
        CancellationToken cancellationToken = default)
    {
        var operation = ValidateOperationId(operationId);
        var requestHash = HashRequest(
            "resolve",
            followUpId,
            expectedVersion.ToString(CultureInfo.InvariantCulture),
            field.ToString(),
            value);
        var replay = await ReplayAsync(
            ownerPrincipalId,
            operation,
            requestHash,
            cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            return replay;
        }

        var current = await RequiredCurrentAsync(
            ownerPrincipalId,
            followUpId,
            expectedVersion,
            cancellationToken).ConfigureAwait(false);
        var conflicts = current.Revisions
            .Where(revision => revision.Field == field
                && revision.State == FollowUpRevisionState.Conflicted)
            .ToArray();
        if (current.Status != FollowUpStatus.Conflict || conflicts.Length < 2)
        {
            throw new InvalidOperationException("Resolution requires an explicit field conflict.");
        }

        var now = DecisionTime(current);
        var evidence = UserEvidence(ownerPrincipalId, operationId, "user.resolution", $"Resolved FollowUp {field} conflict.", now);
        var conflictIds = conflicts.Select(revision => revision.RevisionId).ToArray();
        var revisions = current.Revisions
            .Select(revision => conflictIds.Contains(revision.RevisionId, StringComparer.Ordinal)
                ? revision.WithState(FollowUpRevisionState.Superseded)
                : revision)
            .ToList();
        revisions.Add(UserRevision(operationId, field, value, evidence, now, conflictIds));
        var next = Next(
            current,
            DeriveStatus(revisions),
            revisions,
            Timeline(current, FollowUpTimelineKind.ConflictResolved, field, $"Resolved {field} conflict.", evidence, now));
        return await CommitAsync(
            ownerPrincipalId,
            next,
            expectedVersion,
            operation,
            requestHash,
            null,
            evidence,
            now,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<FollowUpCommitResult?> ReplayAsync(
        string ownerPrincipalId,
        string operationId,
        string requestHash,
        CancellationToken cancellationToken)
    {
        var receipt = await _repository.GetFollowUpOperationAsync(
            ownerPrincipalId,
            operationId,
            cancellationToken).ConfigureAwait(false);
        if (receipt is null)
        {
            return null;
        }

        if (!string.Equals(receipt.RequestHash, requestHash, StringComparison.Ordinal))
        {
            throw new FollowUpOperationConflictException("Operation ID was already used with a different request.");
        }

        var followUp = await _repository.GetFollowUpAsync(
            ownerPrincipalId,
            receipt.FollowUpId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Operation references a missing FollowUp.");
        return new FollowUpCommitResult(followUp, true, receipt.ResultVersion);
    }

    private async Task<FollowUp> RequiredCurrentAsync(
        string ownerPrincipalId,
        string followUpId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var current = await _repository.GetFollowUpAsync(
            ownerPrincipalId,
            ValidateFollowUpId(followUpId),
            cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("FollowUp not found.");
        if (current.Version != expectedVersion)
        {
            throw new FollowUpConcurrencyException("FollowUp version is stale.");
        }

        return current;
    }

    private static FollowUp ApplyExtraction(
        FollowUp? current,
        string followUpId,
        string ownerPrincipalId,
        SourceRecord source,
        EvidenceRecord evidence,
        FollowUpExtraction extraction)
    {
        var revisions = current?.Revisions.ToList() ?? [];
        var timeline = current?.Timeline.ToList() ?? [];
        var stale = false;
        var conflict = false;
        foreach (var field in extraction.Fields)
        {
            var accepted = current?.CurrentField(field.Field);
            var isStale = accepted is not null
                && source.OccurredAt <= accepted.Provenance.SourceTimestamp;
            var state = isStale
                ? FollowUpRevisionState.Rejected
                : field.ConflictsWithCurrent
                    ? FollowUpRevisionState.Conflicted
                    : FollowUpRevisionState.Candidate;
            if (field.ConflictsWithCurrent && !isStale && accepted is not null)
            {
                var index = revisions.FindIndex(revision => revision.RevisionId == accepted.RevisionId);
                revisions[index] = accepted.WithState(FollowUpRevisionState.Conflicted);
                conflict = true;
            }

            stale |= isStale;
            revisions.Add(FollowUpRevision.Create(
                RevisionId(source.SourceNativeId, field.Field),
                field.Field,
                field.Value,
                state,
                FollowUpFieldProvenance.Create(
                    [evidence.EvidenceId],
                    source.OccurredAt,
                    DeterministicFollowUpExtractor.ParserVersion,
                    field.Confidence,
                    lineageRevisionRefs: field.ContextRevisionRefs),
                source.ObservedAt));
        }

        var kind = conflict
            ? FollowUpTimelineKind.ConflictDetected
            : stale
                ? FollowUpTimelineKind.RejectedStale
                : FollowUpTimelineKind.Imported;
        var status = conflict
            ? FollowUpStatus.Conflict
            : extraction.Fields.Any(field => current?.CurrentField(field.Field) is null
                || source.OccurredAt > current.CurrentField(field.Field)!.Provenance.SourceTimestamp)
                ? FollowUpStatus.Attention
                : current?.Status ?? FollowUpStatus.Attention;
        var sequence = (current is null || current.Timeline.Count == 0
            ? 0
            : current.Timeline[^1].Sequence) + 1;
        timeline.Add(FollowUpTimelineEntry.Create(
            sequence,
            kind,
            extraction.Fields.Count == 1 ? extraction.Fields[0].Field : null,
            kind switch
            {
                FollowUpTimelineKind.ConflictDetected => "Detected incompatible source evidence.",
                FollowUpTimelineKind.RejectedStale => "Retained stale source evidence without changing current state.",
                _ => "Imported deterministic source evidence as candidate state.",
            },
            evidence.EvidenceId,
            source.OccurredAt,
            source.ObservedAt));
        return FollowUp.Create(
            followUpId,
            ownerPrincipalId,
            status,
            revisions,
            timeline,
            current?.CreatedAt ?? source.ObservedAt,
            source.ObservedAt,
            (current?.Version ?? 0) + 1);
    }

    private async Task<FollowUpCommitResult> CommitAsync(
        string ownerPrincipalId,
        FollowUp aggregate,
        long? expectedVersion,
        string operationId,
        string requestHash,
        FollowUpSourceIdentity? sourceIdentity,
        EvidenceRecord evidence,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var observationEvent = ObservationEvent.Create(
            $"event:{operationId}",
            ownerPrincipalId,
            aggregate.Timeline[^1].Kind.ToString(),
            occurredAt,
            aggregate.UpdatedAt,
            [ownerPrincipalId],
            [aggregate.FollowUpId],
            [evidence.EvidenceId],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["followUpId"] = aggregate.FollowUpId,
                ["status"] = aggregate.Status.ToString(),
            },
            sourceIdentity is null ? UserDecision : Parser,
            1);
        var assertions = aggregate.Revisions.Select(revision => ToAssertion(aggregate, revision)).ToArray();
        return await _repository.CommitFollowUpAsync(
            ownerPrincipalId,
            new FollowUpCommit(
                aggregate,
                expectedVersion,
                operationId,
                requestHash,
                sourceIdentity,
                evidence,
                observationEvent,
                assertions),
            cancellationToken).ConfigureAwait(false);
    }

    private static FollowUp Next(
        FollowUp current,
        FollowUpStatus status,
        IEnumerable<FollowUpRevision> revisions,
        FollowUpTimelineEntry timeline)
        => FollowUp.Create(
            current.FollowUpId,
            current.OwnerPrincipalId,
            status,
            revisions,
            current.Timeline.Append(timeline),
            current.CreatedAt,
            timeline.RecordedAt,
            current.Version + 1);

    private static FollowUpTimelineEntry Timeline(
        FollowUp current,
        FollowUpTimelineKind kind,
        FollowUpField? field,
        string summary,
        EvidenceRecord evidence,
        DateTimeOffset now)
        => FollowUpTimelineEntry.Create(
            (current.Timeline.Count == 0 ? 0 : current.Timeline[^1].Sequence) + 1,
            kind,
            field,
            summary,
            evidence.EvidenceId,
            now,
            now);

    private static FollowUpRevision UserRevision(
        string operationId,
        FollowUpField field,
        string value,
        EvidenceRecord evidence,
        DateTimeOffset now,
        IEnumerable<string> lineage)
        => FollowUpRevision.Create(
            $"revision:{operationId}:{field}",
            field,
            value,
            FollowUpRevisionState.Current,
            FollowUpFieldProvenance.Create(
                [evidence.EvidenceId],
                now,
                UserDecision.Version,
                1m,
                evidence.EvidenceId,
                lineage),
            now);

    private static EvidenceRecord SourceEvidence(SourceRecord source)
        => EvidenceRecord.Create(
            $"evidence:{source.SourceType}:{source.SourceNativeId}",
            source.OwnerPrincipalId,
            source.SourceType,
            source.SourceNativeId,
            source.SourceLocator,
            source.ObservedAt,
            source.OccurredAt,
            "sha256",
            1,
            Hash(source.Content),
            RetentionState.Active,
            source.Sensitivity,
            Parser,
            1,
            source.Content);

    private static EvidenceRecord UserEvidence(
        string ownerPrincipalId,
        string operationId,
        string sourceType,
        string summary,
        DateTimeOffset now)
        => EvidenceRecord.Create(
            $"evidence:{operationId}",
            ownerPrincipalId,
            sourceType,
            operationId,
            $"tessera://follow-up/{operationId}",
            now,
            now,
            "sha256",
            1,
            Hash(summary),
            RetentionState.Active,
            SensitivityClass.Internal,
            UserDecision,
            1,
            summary);

    private static AssertionRecord ToAssertion(FollowUp aggregate, FollowUpRevision revision)
    {
        var status = revision.State switch
        {
            FollowUpRevisionState.Candidate => EpistemicStatus.Candidate,
            FollowUpRevisionState.Current => EpistemicStatus.Current,
            FollowUpRevisionState.Conflicted => EpistemicStatus.Conflicted,
            FollowUpRevisionState.Superseded => EpistemicStatus.Superseded,
            _ => EpistemicStatus.Rejected,
        };
        var userAsserted = revision.Provenance.CorrectionEvidenceRef is not null;
        DateTimeOffset? supersededAt = status == EpistemicStatus.Superseded
            ? FindSupersededAt(aggregate, revision)
            : null;
        return AssertionRecord.Create(
            revision.RevisionId,
            aggregate.OwnerPrincipalId,
            aggregate.FollowUpId,
            revision.Field.ToString(),
            revision.Value,
            userAsserted ? AssertionType.UserAsserted : AssertionType.Extracted,
            status,
            revision.Provenance.Confidence,
            revision.Provenance.SourceTimestamp,
            supersededAt,
            revision.CreatedAt,
            supersededAt,
            revision.Provenance.EvidenceRefs,
            revision.Provenance.LineageRevisionRefs,
            status.ToString(),
            userAsserted ? UserDecision : Parser,
            1);
    }

    internal static DateTimeOffset FindSupersededAt(FollowUp aggregate, FollowUpRevision revision)
    {
        var transitions = new List<DateTimeOffset>();
        foreach (var descendant in aggregate.Revisions.Where(candidate =>
            candidate.Provenance.LineageRevisionRefs.Contains(revision.RevisionId, StringComparer.Ordinal)))
        {
            if (descendant.Provenance.CorrectionEvidenceRef is not null)
            {
                transitions.Add(descendant.CreatedAt);
                continue;
            }

            var acceptance = aggregate.Timeline.FirstOrDefault(entry =>
                entry.RecordedAt >= descendant.CreatedAt
                && descendant.Provenance.EvidenceRefs.Contains(entry.EvidenceRef, StringComparer.Ordinal)
                && entry.Kind is FollowUpTimelineKind.Accepted or FollowUpTimelineKind.Completed);
            if (acceptance is not null)
            {
                transitions.Add(acceptance.RecordedAt);
            }
        }

        return transitions.Count > 0
            ? transitions.Min()
            : throw new InvalidDataException("Superseded FollowUp revision has no durable transition lineage.");
    }

    private static string RevisionId(string sourceNativeId, FollowUpField field)
        => $"revision:{sourceNativeId}:{field}";

    private DateTimeOffset DecisionTime(FollowUp current)
    {
        var now = _timeProvider.GetUtcNow();
        return now < current.UpdatedAt ? current.UpdatedAt : now;
    }

    private static string ValidateOperationId(string operationId)
        => KernelValidation.VisibleAscii(operationId, nameof(operationId), 128);

    private static string ValidateFollowUpId(string followUpId)
        => KernelValidation.VisibleAscii(followUpId, nameof(followUpId), 128);

    private static FollowUpStatus DeriveStatus(IEnumerable<FollowUpRevision> revisions)
    {
        var state = revisions.ToArray();
        if (state.Any(revision => revision.State == FollowUpRevisionState.Conflicted))
        {
            return FollowUpStatus.Conflict;
        }

        if (state.Any(revision => revision.State == FollowUpRevisionState.Candidate))
        {
            return FollowUpStatus.Attention;
        }

        return state.Any(revision => revision.Field == FollowUpField.CompletedAt
            && revision.State == FollowUpRevisionState.Current)
            ? FollowUpStatus.Completed
            : FollowUpStatus.Tracked;
    }

    private static void EnsureOwner(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Source owner does not match the required principal scope.");
        }
    }

    private static string HashRequest(params string[] parts)
        => Hash(string.Join('\n', parts));

    private static string Hash(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}