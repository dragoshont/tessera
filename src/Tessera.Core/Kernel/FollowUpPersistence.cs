namespace Tessera.Core.Kernel;

public sealed record FollowUpSourceIdentity(
    string SourceType,
    string SourceNativeId,
    string PayloadHash);

public sealed record FollowUpCommit(
    FollowUp Aggregate,
    long? ExpectedVersion,
    string OperationId,
    string RequestHash,
    FollowUpSourceIdentity? SourceIdentity,
    EvidenceRecord Evidence,
    ObservationEvent ObservationEvent,
    IReadOnlyList<AssertionRecord> Assertions);

public sealed record FollowUpOperationReceipt(
    string RequestHash,
    string FollowUpId,
    long ResultVersion);

public sealed record FollowUpSourceReceipt(
    string PayloadHash,
    string FollowUpId,
    long ResultVersion);

public sealed record FollowUpCommitResult(
    FollowUp FollowUp,
    bool Replayed,
    long ResultVersion);

public sealed class FollowUpConcurrencyException(string message) : InvalidOperationException(message);

public sealed class FollowUpOperationConflictException(string message) : InvalidOperationException(message);

public sealed class FollowUpNeedsContextException(string message) : InvalidOperationException(message);

public interface IFollowUpRepository
{
    Task<FollowUpOperationReceipt?> GetFollowUpOperationAsync(
        string ownerPrincipalId,
        string operationId,
        CancellationToken cancellationToken = default);

    Task<FollowUpSourceReceipt?> GetFollowUpSourceAsync(
        string ownerPrincipalId,
        string sourceType,
        string sourceNativeId,
        CancellationToken cancellationToken = default);

    Task RecordFollowUpOperationAsync(
        string ownerPrincipalId,
        string operationId,
        string requestHash,
        string followUpId,
        long resultVersion,
        CancellationToken cancellationToken = default);

    Task<FollowUp?> GetFollowUpAsync(
        string ownerPrincipalId,
        string followUpId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FollowUp>> ListFollowUpsAsync(
        string ownerPrincipalId,
        FollowUpStatus? status = null,
        int limit = 100,
        CancellationToken cancellationToken = default);

    Task<FollowUpCommitResult> CommitFollowUpAsync(
        string ownerPrincipalId,
        FollowUpCommit commit,
        CancellationToken cancellationToken = default);
}
