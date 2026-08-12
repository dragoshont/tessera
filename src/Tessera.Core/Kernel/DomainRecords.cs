namespace Tessera.Core.Kernel;

public enum SensitivityClass
{
    Public,
    Internal,
    Confidential,
    Restricted,
}

public enum RetentionState
{
    Active,
    Redacted,
    Deleted,
    Expired,
}

public sealed record ProducerRef(string Id, string Version)
{
    public static ProducerRef Create(string id, string version)
        => new(KernelValidation.Text(id, nameof(id), 256), KernelValidation.Text(version, nameof(version), 64));
}

public sealed record EvidenceRecord
{
    private EvidenceRecord(
        string evidenceId,
        string ownerPrincipalId,
        string sourceType,
        string sourceNativeId,
        string sourceLocator,
        DateTimeOffset observedAt,
        DateTimeOffset? sourceTimestamp,
        string contentHashAlgorithm,
        int contentHashVersion,
        string contentHash,
        RetentionState retentionState,
        SensitivityClass sensitivity,
        ProducerRef producer,
        int schemaVersion,
        string? boundedExcerpt,
        string? contentReference)
    {
        EvidenceId = evidenceId;
        OwnerPrincipalId = ownerPrincipalId;
        SourceType = sourceType;
        SourceNativeId = sourceNativeId;
        SourceLocator = sourceLocator;
        ObservedAt = observedAt;
        SourceTimestamp = sourceTimestamp;
        ContentHashAlgorithm = contentHashAlgorithm;
        ContentHashVersion = contentHashVersion;
        ContentHash = contentHash;
        RetentionState = retentionState;
        Sensitivity = sensitivity;
        Producer = producer;
        SchemaVersion = schemaVersion;
        BoundedExcerpt = boundedExcerpt;
        ContentReference = contentReference;
    }

    public string EvidenceId { get; }
    public string OwnerPrincipalId { get; }
    public string SourceType { get; }
    public string SourceNativeId { get; }
    public string SourceLocator { get; }
    public DateTimeOffset ObservedAt { get; }
    public DateTimeOffset? SourceTimestamp { get; }
    public string ContentHashAlgorithm { get; }
    public int ContentHashVersion { get; }
    public string ContentHash { get; }
    public RetentionState RetentionState { get; }
    public SensitivityClass Sensitivity { get; }
    public ProducerRef Producer { get; }
    public int SchemaVersion { get; }
    public string? BoundedExcerpt { get; }
    public string? ContentReference { get; }

    public static EvidenceRecord Create(
        string evidenceId,
        string ownerPrincipalId,
        string sourceType,
        string sourceNativeId,
        string sourceLocator,
        DateTimeOffset observedAt,
        DateTimeOffset? sourceTimestamp,
        string contentHashAlgorithm,
        int contentHashVersion,
        string contentHash,
        RetentionState retentionState,
        SensitivityClass sensitivity,
        ProducerRef producer,
        int schemaVersion,
        string? boundedExcerpt = null,
        string? contentReference = null)
    {
        ArgumentNullException.ThrowIfNull(producer);
        ArgumentOutOfRangeException.ThrowIfLessThan(contentHashVersion, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(schemaVersion, 1);
        if (string.Equals(sourceType, "capability.result", StringComparison.Ordinal))
        {
            boundedExcerpt = null;
        }
        if (boundedExcerpt?.Length > 4096)
        {
            throw new ArgumentException("Evidence excerpt cannot exceed 4096 characters.", nameof(boundedExcerpt));
        }

        return new EvidenceRecord(
            KernelValidation.Text(evidenceId, nameof(evidenceId), 256),
            KernelValidation.Text(ownerPrincipalId, nameof(ownerPrincipalId), 256),
            KernelValidation.Text(sourceType, nameof(sourceType), 128),
            KernelValidation.Text(sourceNativeId, nameof(sourceNativeId), 512),
            KernelValidation.Text(sourceLocator, nameof(sourceLocator), 2048),
            KernelValidation.Timestamp(observedAt, nameof(observedAt)),
            sourceTimestamp?.ToUniversalTime(),
            KernelValidation.Text(contentHashAlgorithm, nameof(contentHashAlgorithm), 64),
            contentHashVersion,
            KernelValidation.Text(contentHash, nameof(contentHash), 512),
            retentionState,
            sensitivity,
            producer,
            schemaVersion,
            KernelValidation.PersistedNonSecretText(boundedExcerpt, nameof(boundedExcerpt), 4096),
            contentReference is null ? null : KernelValidation.Text(contentReference, nameof(contentReference), 2048));
    }
}

public sealed record ObservationEvent
{
    private ObservationEvent(
        string eventId,
        string ownerPrincipalId,
        string eventType,
        DateTimeOffset occurredAt,
        DateTimeOffset observedAt,
        IReadOnlyList<string> actorRefs,
        IReadOnlyList<string> objectRefs,
        IReadOnlyList<string> evidenceRefs,
        IReadOnlyDictionary<string, string> attributes,
        ProducerRef producer,
        int schemaVersion)
    {
        EventId = eventId;
        OwnerPrincipalId = ownerPrincipalId;
        EventType = eventType;
        OccurredAt = occurredAt;
        ObservedAt = observedAt;
        ActorRefs = actorRefs;
        ObjectRefs = objectRefs;
        EvidenceRefs = evidenceRefs;
        Attributes = attributes;
        Producer = producer;
        SchemaVersion = schemaVersion;
    }

    public string EventId { get; }
    public string OwnerPrincipalId { get; }
    public string EventType { get; }
    public DateTimeOffset OccurredAt { get; }
    public DateTimeOffset ObservedAt { get; }
    public IReadOnlyList<string> ActorRefs { get; }
    public IReadOnlyList<string> ObjectRefs { get; }
    public IReadOnlyList<string> EvidenceRefs { get; }
    public IReadOnlyDictionary<string, string> Attributes { get; }
    public ProducerRef Producer { get; }
    public int SchemaVersion { get; }

    public static ObservationEvent Create(
        string eventId,
        string ownerPrincipalId,
        string eventType,
        DateTimeOffset occurredAt,
        DateTimeOffset observedAt,
        IEnumerable<string> actorRefs,
        IEnumerable<string> objectRefs,
        IEnumerable<string> evidenceRefs,
        IReadOnlyDictionary<string, string> attributes,
        ProducerRef producer,
        int schemaVersion)
    {
        ArgumentNullException.ThrowIfNull(producer);
        ArgumentOutOfRangeException.ThrowIfLessThan(schemaVersion, 1);
        return new ObservationEvent(
            KernelValidation.Text(eventId, nameof(eventId), 256),
            KernelValidation.Text(ownerPrincipalId, nameof(ownerPrincipalId), 256),
            KernelValidation.Text(eventType, nameof(eventType), 128),
            KernelValidation.Timestamp(occurredAt, nameof(occurredAt)),
            KernelValidation.Timestamp(observedAt, nameof(observedAt)),
            KernelValidation.References(actorRefs, nameof(actorRefs)),
            KernelValidation.References(objectRefs, nameof(objectRefs)),
            KernelValidation.References(evidenceRefs, nameof(evidenceRefs)),
            KernelValidation.Attributes(attributes, nameof(attributes)),
            producer,
            schemaVersion);
    }
}

public sealed record WorkflowCheckpoint
{
    private WorkflowCheckpoint(
        string workflowId,
        string ownerPrincipalId,
        string workflowType,
        string state,
        string currentStep,
        IReadOnlyList<string> inputRefs,
        IReadOnlyList<string> outputRefs,
        string? wakeCondition,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        long version)
    {
        WorkflowId = workflowId;
        OwnerPrincipalId = ownerPrincipalId;
        WorkflowType = workflowType;
        State = state;
        CurrentStep = currentStep;
        InputRefs = inputRefs;
        OutputRefs = outputRefs;
        WakeCondition = wakeCondition;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        Version = version;
    }

    public string WorkflowId { get; }
    public string OwnerPrincipalId { get; }
    public string WorkflowType { get; }
    public string State { get; }
    public string CurrentStep { get; }
    public IReadOnlyList<string> InputRefs { get; }
    public IReadOnlyList<string> OutputRefs { get; }
    public string? WakeCondition { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; }
    public long Version { get; }

    public static WorkflowCheckpoint Create(
        string workflowId,
        string ownerPrincipalId,
        string workflowType,
        string state,
        string currentStep,
        IEnumerable<string> inputRefs,
        IEnumerable<string> outputRefs,
        string? wakeCondition,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        long version)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(version);
        return new WorkflowCheckpoint(
            KernelValidation.Text(workflowId, nameof(workflowId), 256),
            KernelValidation.Text(ownerPrincipalId, nameof(ownerPrincipalId), 256),
            KernelValidation.Text(workflowType, nameof(workflowType), 128),
            KernelValidation.Text(state, nameof(state), 128),
            KernelValidation.Text(currentStep, nameof(currentStep), 256),
            KernelValidation.References(inputRefs, nameof(inputRefs)),
            KernelValidation.References(outputRefs, nameof(outputRefs)),
            wakeCondition is null ? null : KernelValidation.Text(wakeCondition, nameof(wakeCondition), 2048),
            KernelValidation.Timestamp(createdAt, nameof(createdAt)),
            KernelValidation.Timestamp(updatedAt, nameof(updatedAt)),
            version);
    }
}