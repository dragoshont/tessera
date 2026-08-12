namespace Tessera.Core.Kernel;

public enum FollowUpStatus
{
    Attention,
    Tracked,
    Conflict,
    Completed,
}

public enum FollowUpField
{
    Deliverable,
    Counterparty,
    DueAt,
    CompletedAt,
}

public enum FollowUpRevisionState
{
    Candidate,
    Current,
    Conflicted,
    Superseded,
    Rejected,
}

public enum FollowUpTimelineKind
{
    Imported,
    Accepted,
    Corrected,
    ConflictDetected,
    ConflictResolved,
    Completed,
    RejectedStale,
}

public sealed record FollowUpFieldProvenance
{
    private FollowUpFieldProvenance(
        IReadOnlyList<string> evidenceRefs,
        DateTimeOffset sourceTimestamp,
        string parserVersion,
        decimal confidence,
        string? correctionEvidenceRef,
        IReadOnlyList<string> lineageRevisionRefs)
    {
        EvidenceRefs = evidenceRefs;
        SourceTimestamp = sourceTimestamp;
        ParserVersion = parserVersion;
        Confidence = confidence;
        CorrectionEvidenceRef = correctionEvidenceRef;
        LineageRevisionRefs = lineageRevisionRefs;
    }

    public IReadOnlyList<string> EvidenceRefs { get; }
    public DateTimeOffset SourceTimestamp { get; }
    public string ParserVersion { get; }
    public decimal Confidence { get; }
    public string? CorrectionEvidenceRef { get; }
    public IReadOnlyList<string> LineageRevisionRefs { get; }

    public static FollowUpFieldProvenance Create(
        IEnumerable<string> evidenceRefs,
        DateTimeOffset sourceTimestamp,
        string parserVersion,
        decimal confidence,
        string? correctionEvidenceRef = null,
        IEnumerable<string>? lineageRevisionRefs = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(confidence, 0m);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(confidence, 1m);
        var evidence = KernelValidation.References(evidenceRefs, nameof(evidenceRefs));
        if (evidence.Count == 0)
        {
            throw new ArgumentException("Follow-up field provenance requires evidence.", nameof(evidenceRefs));
        }

        return new FollowUpFieldProvenance(
            evidence,
            KernelValidation.Timestamp(sourceTimestamp, nameof(sourceTimestamp)),
            KernelValidation.Text(parserVersion, nameof(parserVersion), 64),
            confidence,
            correctionEvidenceRef is null
                ? null
                : KernelValidation.Text(correctionEvidenceRef, nameof(correctionEvidenceRef), 256),
            KernelValidation.References(lineageRevisionRefs ?? [], nameof(lineageRevisionRefs)));
    }
}

public sealed record FollowUpRevision
{
    private FollowUpRevision(
        string revisionId,
        FollowUpField field,
        string value,
        FollowUpRevisionState state,
        FollowUpFieldProvenance provenance,
        DateTimeOffset createdAt)
    {
        RevisionId = revisionId;
        Field = field;
        Value = value;
        State = state;
        Provenance = provenance;
        CreatedAt = createdAt;
    }

    public string RevisionId { get; }
    public FollowUpField Field { get; }
    public string Value { get; }
    public FollowUpRevisionState State { get; }
    public FollowUpFieldProvenance Provenance { get; }
    public DateTimeOffset CreatedAt { get; }

    public static FollowUpRevision Create(
        string revisionId,
        FollowUpField field,
        string value,
        FollowUpRevisionState state,
        FollowUpFieldProvenance provenance,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        var maximumLength = field switch
        {
            FollowUpField.Deliverable => 256,
            FollowUpField.Counterparty => 128,
            _ => 64,
        };
        var normalizedValue = KernelValidation.PersistedNonSecretText(value, nameof(value), maximumLength)
            ?? throw new ArgumentException("Follow-up value is required.", nameof(value));
        if (field == FollowUpField.DueAt
            && !DateOnly.TryParseExact(normalizedValue, "yyyy-MM-dd", out _))
        {
            throw new ArgumentException("Follow-up due date must use yyyy-MM-dd.", nameof(value));
        }

        if (field == FollowUpField.CompletedAt
            && (!DateTimeOffset.TryParseExact(
                    normalizedValue,
                    "O",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var completedAt)
                || completedAt.Offset != TimeSpan.Zero))
        {
            throw new ArgumentException("Follow-up completion must be an ISO-8601 timestamp.", nameof(value));
        }

        return new FollowUpRevision(
            KernelValidation.VisibleAscii(revisionId, nameof(revisionId), 256),
            field,
            normalizedValue,
            state,
            provenance,
            KernelValidation.Timestamp(createdAt, nameof(createdAt)));
    }

    public FollowUpRevision WithState(
        FollowUpRevisionState state,
        FollowUpFieldProvenance? provenance = null)
        => Create(RevisionId, Field, Value, state, provenance ?? Provenance, CreatedAt);
}

public sealed record FollowUpTimelineEntry
{
    private FollowUpTimelineEntry(
        long sequence,
        FollowUpTimelineKind kind,
        FollowUpField? field,
        string summary,
        string evidenceRef,
        DateTimeOffset sourceTimestamp,
        DateTimeOffset recordedAt)
    {
        Sequence = sequence;
        Kind = kind;
        Field = field;
        Summary = summary;
        EvidenceRef = evidenceRef;
        SourceTimestamp = sourceTimestamp;
        RecordedAt = recordedAt;
    }

    public long Sequence { get; }
    public FollowUpTimelineKind Kind { get; }
    public FollowUpField? Field { get; }
    public string Summary { get; }
    public string EvidenceRef { get; }
    public DateTimeOffset SourceTimestamp { get; }
    public DateTimeOffset RecordedAt { get; }

    public static FollowUpTimelineEntry Create(
        long sequence,
        FollowUpTimelineKind kind,
        FollowUpField? field,
        string summary,
        string evidenceRef,
        DateTimeOffset sourceTimestamp,
        DateTimeOffset recordedAt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        return new FollowUpTimelineEntry(
            sequence,
            kind,
            field,
            KernelValidation.Text(summary, nameof(summary), 512),
            KernelValidation.Text(evidenceRef, nameof(evidenceRef), 256),
            KernelValidation.Timestamp(sourceTimestamp, nameof(sourceTimestamp)),
            KernelValidation.Timestamp(recordedAt, nameof(recordedAt)));
    }
}

public sealed record FollowUp
{
    private FollowUp(
        string followUpId,
        string ownerPrincipalId,
        FollowUpStatus status,
        IReadOnlyList<FollowUpRevision> revisions,
        IReadOnlyList<FollowUpTimelineEntry> timeline,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        long version)
    {
        FollowUpId = followUpId;
        OwnerPrincipalId = ownerPrincipalId;
        Status = status;
        Revisions = revisions;
        Timeline = timeline;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        Version = version;
    }

    public string FollowUpId { get; }
    public string OwnerPrincipalId { get; }
    public FollowUpStatus Status { get; }
    public IReadOnlyList<FollowUpRevision> Revisions { get; }
    public IReadOnlyList<FollowUpTimelineEntry> Timeline { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; }
    public long Version { get; }

    public IReadOnlyList<FollowUpRevision> Current => Revisions
        .Where(revision => revision.State == FollowUpRevisionState.Current)
        .ToArray();

    public IReadOnlyList<FollowUpRevision> Candidates => Revisions
        .Where(revision => revision.State == FollowUpRevisionState.Candidate)
        .ToArray();

    public static FollowUp Create(
        string followUpId,
        string ownerPrincipalId,
        FollowUpStatus status,
        IEnumerable<FollowUpRevision> revisions,
        IEnumerable<FollowUpTimelineEntry> timeline,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        long version)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(version);
        var normalizedCreatedAt = KernelValidation.Timestamp(createdAt, nameof(createdAt));
        var normalizedUpdatedAt = KernelValidation.Timestamp(updatedAt, nameof(updatedAt));
        if (normalizedUpdatedAt < normalizedCreatedAt)
        {
            throw new ArgumentException("Follow-up update cannot predate creation.", nameof(updatedAt));
        }

        var revisionList = revisions?.ToArray() ?? throw new ArgumentNullException(nameof(revisions));
        foreach (var fieldGroup in revisionList.GroupBy(revision => revision.Field))
        {
            if (fieldGroup.Count(revision => revision.State == FollowUpRevisionState.Current) > 1)
            {
                throw new ArgumentException("A follow-up field can have only one current revision.", nameof(revisions));
            }
        }

        return new FollowUp(
            KernelValidation.VisibleAscii(followUpId, nameof(followUpId), 128),
            KernelValidation.Text(ownerPrincipalId, nameof(ownerPrincipalId), 256),
            status,
            Array.AsReadOnly(revisionList),
            Array.AsReadOnly((timeline ?? throw new ArgumentNullException(nameof(timeline)))
                .OrderBy(entry => entry.Sequence)
                .ToArray()),
            normalizedCreatedAt,
            normalizedUpdatedAt,
            version);
    }

    public FollowUpRevision? CurrentField(FollowUpField field)
        => Current.SingleOrDefault(revision => revision.Field == field);
}
