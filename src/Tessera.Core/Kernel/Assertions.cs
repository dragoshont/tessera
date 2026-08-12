namespace Tessera.Core.Kernel;

public enum AssertionType
{
    UserAsserted,
    SourceAsserted,
    Extracted,
    Inferred,
    Derived,
    System,
}

public enum EpistemicStatus
{
    Candidate,
    Supported,
    Current,
    Conflicted,
    Superseded,
    Rejected,
}

public sealed record AssertionRecord
{
    private AssertionRecord(
        string assertionId,
        string ownerPrincipalId,
        string subjectKey,
        string predicate,
        string value,
        AssertionType assertionType,
        EpistemicStatus epistemicStatus,
        decimal confidence,
        DateTimeOffset validFrom,
        DateTimeOffset? validTo,
        DateTimeOffset createdAt,
        DateTimeOffset? supersededAt,
        IReadOnlyList<string> evidenceRefs,
        IReadOnlyList<string> lineageRefs,
        string? promotionReason,
        ProducerRef producer,
        int schemaVersion)
    {
        AssertionId = assertionId;
        OwnerPrincipalId = ownerPrincipalId;
        SubjectKey = subjectKey;
        Predicate = predicate;
        Value = value;
        AssertionType = assertionType;
        EpistemicStatus = epistemicStatus;
        Confidence = confidence;
        ValidFrom = validFrom;
        ValidTo = validTo;
        CreatedAt = createdAt;
        SupersededAt = supersededAt;
        EvidenceRefs = evidenceRefs;
        LineageRefs = lineageRefs;
        PromotionReason = promotionReason;
        Producer = producer;
        SchemaVersion = schemaVersion;
    }

    public string AssertionId { get; }
    public string OwnerPrincipalId { get; }
    public string SubjectKey { get; }
    public string Predicate { get; }
    public string Value { get; }
    public AssertionType AssertionType { get; }
    public EpistemicStatus EpistemicStatus { get; }
    public decimal Confidence { get; }
    public DateTimeOffset ValidFrom { get; }
    public DateTimeOffset? ValidTo { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? SupersededAt { get; }
    public IReadOnlyList<string> EvidenceRefs { get; }
    public IReadOnlyList<string> LineageRefs { get; }
    public string? PromotionReason { get; }
    public ProducerRef Producer { get; }
    public int SchemaVersion { get; }

    public static AssertionRecord Create(
        string assertionId,
        string ownerPrincipalId,
        string subjectKey,
        string predicate,
        string value,
        AssertionType assertionType,
        EpistemicStatus epistemicStatus,
        decimal confidence,
        DateTimeOffset validFrom,
        DateTimeOffset? validTo,
        DateTimeOffset createdAt,
        DateTimeOffset? supersededAt,
        IEnumerable<string> evidenceRefs,
        IEnumerable<string> lineageRefs,
        string? promotionReason,
        ProducerRef producer,
        int schemaVersion)
    {
        ArgumentNullException.ThrowIfNull(producer);
        ArgumentOutOfRangeException.ThrowIfLessThan(confidence, 0m);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(confidence, 1m);
        ArgumentOutOfRangeException.ThrowIfLessThan(schemaVersion, 1);
        var evidence = KernelValidation.References(evidenceRefs, nameof(evidenceRefs));
        var lineage = KernelValidation.References(lineageRefs, nameof(lineageRefs));
        if (evidence.Count == 0 && lineage.Count == 0)
        {
            throw new ArgumentException("An assertion requires evidence or explicit derivation lineage.", nameof(evidenceRefs));
        }

        var normalizedValidFrom = KernelValidation.Timestamp(validFrom, nameof(validFrom));
        var normalizedCreatedAt = KernelValidation.Timestamp(createdAt, nameof(createdAt));
        if (validTo is { } validToValue && validToValue.ToUniversalTime() < normalizedValidFrom)
        {
            throw new ArgumentException("Assertion validity cannot end before it begins.", nameof(validTo));
        }

        if (supersededAt is { } supersededValue && supersededValue.ToUniversalTime() < normalizedCreatedAt)
        {
            throw new ArgumentException("Assertion supersession cannot predate creation.", nameof(supersededAt));
        }

        return new AssertionRecord(
            KernelValidation.Text(assertionId, nameof(assertionId), 256),
            KernelValidation.Text(ownerPrincipalId, nameof(ownerPrincipalId), 256),
            KernelValidation.Text(subjectKey, nameof(subjectKey), 512),
            KernelValidation.Text(predicate, nameof(predicate), 256),
            KernelValidation.Text(value, nameof(value), 16384),
            assertionType,
            epistemicStatus,
            confidence,
            normalizedValidFrom,
            validTo?.ToUniversalTime(),
            normalizedCreatedAt,
            supersededAt?.ToUniversalTime(),
            evidence,
            lineage,
            promotionReason is null ? null : KernelValidation.Text(promotionReason, nameof(promotionReason), 1024),
            producer,
            schemaVersion);
    }
}

public static class AssertionService
{
    public static AssertionRecord PromoteCandidate(
        AssertionRecord candidate,
        string promotionReason)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.EpistemicStatus is not (EpistemicStatus.Candidate or EpistemicStatus.Supported))
        {
            throw new InvalidOperationException("Only candidate or supported assertions can become current.");
        }

        return Copy(candidate, EpistemicStatus.Current, null, null, promotionReason);
    }

    public static (AssertionRecord Superseded, AssertionRecord Current) Correct(
        AssertionRecord current,
        AssertionRecord correction,
        DateTimeOffset correctedAt,
        string promotionReason)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(correction);
        if (current.EpistemicStatus != EpistemicStatus.Current
            || correction.AssertionType != AssertionType.UserAsserted
            || !HasSameKey(current, correction)
            || !string.Equals(current.OwnerPrincipalId, correction.OwnerPrincipalId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Correction must be a user assertion for the same current owner and key.");
        }

        var at = KernelValidation.Timestamp(correctedAt, nameof(correctedAt));
        return (
            Copy(current, EpistemicStatus.Superseded, at, at, current.PromotionReason),
            Copy(correction, EpistemicStatus.Current, null, null, promotionReason));
    }

    public static (AssertionRecord First, AssertionRecord Second) MarkConflict(
        AssertionRecord first,
        AssertionRecord second,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        if (!HasSameKey(first, second)
            || !string.Equals(first.OwnerPrincipalId, second.OwnerPrincipalId, StringComparison.Ordinal)
            || string.Equals(first.Value, second.Value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Conflict requires different values for the same owner and key.");
        }

        return (
            Copy(first, EpistemicStatus.Conflicted, null, null, reason),
            Copy(second, EpistemicStatus.Conflicted, null, null, reason));
    }

    private static bool HasSameKey(AssertionRecord first, AssertionRecord second)
        => string.Equals(first.SubjectKey, second.SubjectKey, StringComparison.Ordinal)
            && string.Equals(first.Predicate, second.Predicate, StringComparison.Ordinal);

    private static AssertionRecord Copy(
        AssertionRecord source,
        EpistemicStatus status,
        DateTimeOffset? validTo,
        DateTimeOffset? supersededAt,
        string? promotionReason)
        => AssertionRecord.Create(
            source.AssertionId,
            source.OwnerPrincipalId,
            source.SubjectKey,
            source.Predicate,
            source.Value,
            source.AssertionType,
            status,
            source.Confidence,
            source.ValidFrom,
            validTo ?? source.ValidTo,
            source.CreatedAt,
            supersededAt,
            source.EvidenceRefs,
            source.LineageRefs,
            promotionReason,
            source.Producer,
            source.SchemaVersion);
}