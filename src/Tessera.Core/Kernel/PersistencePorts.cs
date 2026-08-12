namespace Tessera.Core.Kernel;

public interface IPrincipalRepository
{
    Task AddAsync(PrincipalRef principal, CancellationToken cancellationToken = default);

    Task<PrincipalRef?> GetAsync(
        string ownerPrincipalId,
        CancellationToken cancellationToken = default);
}

public interface IEvidenceRepository
{
    Task AddAsync(
        string ownerPrincipalId,
        EvidenceRecord evidence,
        CancellationToken cancellationToken = default);

    Task<EvidenceRecord?> GetAsync(
        string ownerPrincipalId,
        string evidenceId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EvidenceRecord>> ListAsync(
        string ownerPrincipalId,
        CancellationToken cancellationToken = default);

    Task<bool> UpdateRetentionAsync(
        string ownerPrincipalId,
        string evidenceId,
        RetentionState retentionState,
        CancellationToken cancellationToken = default);
}

public interface IEventRepository
{
    Task AppendAsync(
        string ownerPrincipalId,
        ObservationEvent observationEvent,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ObservationEvent>> ListAsync(
        string ownerPrincipalId,
        CancellationToken cancellationToken = default);
}

public interface IAssertionRepository
{
    Task SaveBatchAsync(
        string ownerPrincipalId,
        IReadOnlyCollection<AssertionRecord> assertions,
        CancellationToken cancellationToken = default);

    Task<AssertionRecord?> GetAsync(
        string ownerPrincipalId,
        string assertionId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AssertionRecord>> ListCurrentAsync(
        string ownerPrincipalId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AssertionRecord>> ListHistoryAsync(
        string ownerPrincipalId,
        string subjectKey,
        string predicate,
        CancellationToken cancellationToken = default);

    Task ApplyCorrectionAsync(
        string ownerPrincipalId,
        AssertionRecord superseded,
        AssertionRecord current,
        CancellationToken cancellationToken = default);
}

public interface IActionRepository
{
    Task AddAsync(
        string ownerPrincipalId,
        ActionRecord action,
        CancellationToken cancellationToken = default);

    Task<ActionRecord?> GetAsync(
        string ownerPrincipalId,
        string actionId,
        CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(
        string ownerPrincipalId,
        ActionRecord action,
        long expectedVersion,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActionRecord>> ListByStateAsync(
        string ownerPrincipalId,
        ActionState state,
        CancellationToken cancellationToken = default);
}

public interface IActionAuthorizationRepository
{
    Task<ActionAuthorization?> GetAsync(string ownerPrincipalId,string authorizationId,CancellationToken cancellationToken=default);

    Task AddAsync(
        string ownerPrincipalId,
        ActionAuthorization authorization,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates and consumes an exact authorization while durably reserving the
    /// matching proposed action. Implementations must perform both mutations in
    /// one transaction so a crash cannot burn approval without authorizing work.
    /// </summary>
    Task<ActionRecord?> TryConsumeAndAuthorizeAsync(
        string ownerPrincipalId,
        string authorizationId,
        ActionRecord proposedAction,
        DateTimeOffset authorizedAt,
        CancellationToken cancellationToken = default);
}

public interface IActionExecutionRepository
{
    Task<ActionRecord?> TryStartAuthorizedAsync(
        string ownerPrincipalId,
        string actionId,
        long expectedVersion,
        string? authorizationId,
        string capabilityId,
        string capabilityVersion,
        string payloadHash,
        string targetScope,
        string? idempotencyKey,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default);
}

public interface IWorkflowRepository
{
    Task AddAsync(
        string ownerPrincipalId,
        WorkflowCheckpoint checkpoint,
        CancellationToken cancellationToken = default);

    Task<WorkflowCheckpoint?> GetAsync(
        string ownerPrincipalId,
        string workflowId,
        CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(
        string ownerPrincipalId,
        WorkflowCheckpoint checkpoint,
        long expectedVersion,
        CancellationToken cancellationToken = default);
}

public interface IKernelObservationRepository
{
    Task AddObservationAsync(
        string ownerPrincipalId,
        EvidenceRecord evidence,
        ObservationEvent observationEvent,
        AssertionRecord candidateAssertion,
        CancellationToken cancellationToken = default);
}