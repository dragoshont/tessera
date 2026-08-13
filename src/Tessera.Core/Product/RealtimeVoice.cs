namespace Tessera.Core.Product;

public static class RealtimeVoiceLimits
{
    public const int MaximumSdpBytes = 64 * 1024;
    public const int DefaultSessionSeconds = 900;

    public static int ClampSessionSeconds(int value) => Math.Clamp(value, 60, 3600);
}

public sealed record RealtimeSessionReceipt(
    string OwnerPrincipalId, string SessionId, string ConversationId, string ClientAttemptId,
    string IdempotencyKeyHash, string OfferHash, string State, long NegotiationGeneration,
    DateTimeOffset NegotiationDeadline, string ProviderModelId, string ProviderModelVersion,
    string ProviderDeploymentRef, DateTimeOffset? NegotiatedAt, DateTimeOffset ExpiresAt,
    DateTimeOffset? EndedAt, string? EndReason, string? FailureCode, long Version);

public sealed record RealtimeSessionTool(
    string OwnerPrincipalId, string SessionId, string ExposedName, string PluginId,
    string PluginVersion, string CapabilityId, string CapabilityVersion, string? AccountId,
    string SchemaHash, string SideEffectClass);

public sealed record RealtimeTurnReceipt(
    string OwnerPrincipalId, string SessionId, string ClientTurnId, string InputItemId,
    string? OutputItemId, string UserMessageId, string? AssistantMessageId,
    string AssistantDisposition, DateTimeOffset CreatedAt);

public sealed record RealtimeToolBinding(
    string OwnerPrincipalId, string SessionId, string ClientCallId, string? CapabilityCallId,
    string? CapabilityResultId, string? ActionId, string State, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, long Version);

public sealed record RealtimeToolCallReservation(
    RealtimeToolBinding Binding, string IdempotencyKey, string RequestHash);

public sealed record RealtimeTurnWrite(
    string OwnerPrincipalId, string ConversationId, string SessionId, string IdempotencyKey,
    string RequestHash, RealtimeTurnReceipt Receipt, ChatMessage UserMessage,
    ChatMessage? AssistantMessage, IReadOnlyList<PublicExecutionEvent> Events);

public sealed record RealtimeEndResult(string SessionId, string Reason, long Version, bool Replayed);

public interface IRealtimeVoiceRepository
{
    Task<RealtimeSessionReceipt?> GetRealtimeSessionAsync(
        string ownerPrincipalId, string sessionId, CancellationToken cancellationToken = default);

    Task<RealtimeSessionReceipt?> GetRealtimeSessionByAttemptAsync(
        string ownerPrincipalId, string clientAttemptId, CancellationToken cancellationToken = default);

    Task<bool> BeginRealtimeNegotiationAsync(
        RealtimeSessionReceipt receipt, IReadOnlyList<RealtimeSessionTool> tools,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RealtimeSessionTool>> ListRealtimeSessionToolsAsync(
        string ownerPrincipalId, string sessionId, CancellationToken cancellationToken = default);

    Task<RealtimeToolBinding?> GetRealtimeToolBindingAsync(
        string ownerPrincipalId, string sessionId, string clientCallId,
        CancellationToken cancellationToken = default);

    Task<bool> BeginRealtimeToolCallAsync(
        RealtimeToolCallReservation reservation, CancellationToken cancellationToken = default);

    Task<bool> CompleteRealtimeToolCallAsync(
        RealtimeToolCallReservation reservation, RealtimeToolBinding completed,
        int responseStatus, string responseBodyJson, CancellationToken cancellationToken = default);

    Task<bool> CompleteRealtimeNegotiationAsync(
        string ownerPrincipalId, string sessionId, long generation, DateTimeOffset negotiatedAt,
        DateTimeOffset expiresAt, CancellationToken cancellationToken = default);

    Task<bool> FailRealtimeNegotiationAsync(
        string ownerPrincipalId, string sessionId, long generation, string failureCode,
        CancellationToken cancellationToken = default);

    Task<int> FenceExpiredRealtimeNegotiationsAsync(
        DateTimeOffset now, CancellationToken cancellationToken = default);

    Task<int> CountOpenRealtimeSessionsAsync(
        string? ownerPrincipalId, DateTimeOffset now, CancellationToken cancellationToken = default);

    Task<RealtimeTurnReceipt?> GetRealtimeTurnAsync(
        string ownerPrincipalId, string sessionId, string clientTurnId,
        CancellationToken cancellationToken = default);

    Task<bool> SaveRealtimeTurnAsync(
        RealtimeTurnWrite write, CancellationToken cancellationToken = default);

    Task<RealtimeEndResult?> EndRealtimeSessionAsync(
        string ownerPrincipalId, string sessionId, string reason, string idempotencyKey,
        string requestHash, DateTimeOffset endedAt,
        CancellationToken cancellationToken = default);
}