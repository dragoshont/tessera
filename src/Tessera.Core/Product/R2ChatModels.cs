using Tessera.Core.Kernel;

namespace Tessera.Core.Product;

public sealed record Conversation(string OwnerPrincipalId, string ConversationId, string Title, string State,
    string? ModelProfileId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, long Version);

public sealed record ChatMessage(string OwnerPrincipalId, string MessageId, string ConversationId, string Role,
    string Status, string? RetryOf, IReadOnlyList<ChatMessagePart> Parts, DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt, long Version);

public sealed record ChatMessagePart(string PartId, long Sequence, string Kind, string? Text,
    string? CapabilityCallId = null, string? CapabilityResultId = null, string? ActionId = null,
    IReadOnlyList<string>? EvidenceRefs = null, string? ErrorCode = null);

public sealed record PublicExecutionEvent(string OwnerPrincipalId, string EventId, string ExecutionId, long Sequence,
    string EventType, DateTimeOffset OccurredAt, string? MessageId, string? CapabilityCallId,
    string? ActionId, string DataJson);

public sealed record PendingChatExecution(string OwnerPrincipalId,string ExecutionId,string ConversationId,
    string UserMessageId,string Text,string ModelProfileId,string IdempotencyKey);

public sealed record ProductCapabilityCall(string OwnerPrincipalId,string CallId,string ExecutionId,string? ConversationId,
    string? MessageId,string? JobId,string? JobRunId,string PluginId,string PluginVersion,string CapabilityId,
    string CapabilityVersion,string? AccountId,string InputJson,string InputHash,string State,DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,string? ErrorCode,long Version,string? ExternalServerId=null,string? ExternalServerName=null,
    string? ExternalServerVersion=null,string? ExternalToolName=null);

public sealed record ProductCapabilityResult(string OwnerPrincipalId,string ResultId,string CallId,string Summary,
    string DataJson,IReadOnlyList<string> EvidenceRefs,bool Truncated,DateTimeOffset CreatedAt);

public interface ICapabilityTraceRepository
{
    Task BeginCapabilityCallAsync(ExecutionRequest request,DateTimeOffset now,CancellationToken token=default);
    Task<CapabilityResult?> GetCompletedCapabilityResultAsync(ExecutionRequest request,CancellationToken token=default);
    Task<bool> TryStartCapabilityCallAsync(ExecutionRequest request,DateTimeOffset now,CancellationToken token=default);
    Task CompleteCapabilityCallAsync(ExecutionRequest request,CapabilityResult result,DateTimeOffset now,CancellationToken token=default);
    Task<IReadOnlyList<ProductCapabilityCall>> ListCapabilityCallsAsync(string owner,string? jobRunId,CancellationToken token=default);
    Task<IReadOnlyList<ProductCapabilityResult>> ListCapabilityResultsAsync(string owner,string? jobRunId,CancellationToken token=default);
}