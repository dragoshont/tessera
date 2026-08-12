using System.Security.Cryptography;

namespace Tessera.Core.Kernel;

public enum ActionState
{
    Proposed,
    Authorized,
    Started,
    ExecutionSucceeded,
    ProviderVerified,
    ExternallyConfirmed,
    Failed,
    Canceled,
    Expired,
    ReconciliationRequired,
}

public static class ActionStateNames
{
    public static string ToContractValue(this ActionState state) => state switch
    {
        ActionState.Proposed => "PROPOSED",
        ActionState.Authorized => "AUTHORIZED",
        ActionState.Started => "STARTED",
        ActionState.ExecutionSucceeded => "EXECUTION_SUCCEEDED",
        ActionState.ProviderVerified => "PROVIDER_VERIFIED",
        ActionState.ExternallyConfirmed => "EXTERNALLY_CONFIRMED",
        ActionState.Failed => "FAILED",
        ActionState.Canceled => "CANCELED",
        ActionState.Expired => "EXPIRED",
        ActionState.ReconciliationRequired => "RECONCILIATION_REQUIRED",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };
}

public sealed record ActionRecord
{
    public ActionR2Binding? R2Binding { get; init; }
    private ActionRecord(
        string actionId,
        string ownerPrincipalId,
        string capabilityId,
        string capabilityVersion,
        string intent,
        string payloadHash,
        string targetScope,
        string riskClass,
        string policyDecisionRef,
        string? authorizationRef,
        ActionState state,
        string idempotencyKey,
        int attemptCount,
        DateTimeOffset createdAt,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt,
        string? providerReceipt,
        string? verificationState,
        string? failure,
        int schemaVersion,
        long version)
    {
        ActionId = actionId;
        OwnerPrincipalId = ownerPrincipalId;
        CapabilityId = capabilityId;
        CapabilityVersion = capabilityVersion;
        Intent = intent;
        PayloadHash = payloadHash;
        TargetScope = targetScope;
        RiskClass = riskClass;
        PolicyDecisionRef = policyDecisionRef;
        AuthorizationRef = authorizationRef;
        State = state;
        IdempotencyKey = idempotencyKey;
        AttemptCount = attemptCount;
        CreatedAt = createdAt;
        StartedAt = startedAt;
        CompletedAt = completedAt;
        ProviderReceipt = providerReceipt;
        VerificationState = verificationState;
        Failure = failure;
        SchemaVersion = schemaVersion;
        Version = version;
    }

    public string ActionId { get; }
    public string OwnerPrincipalId { get; }
    public string CapabilityId { get; }
    public string CapabilityVersion { get; }
    public string Intent { get; }
    public string PayloadHash { get; }
    public string TargetScope { get; }
    public string RiskClass { get; }
    public string PolicyDecisionRef { get; }
    public string? AuthorizationRef { get; }
    public ActionState State { get; }
    public string IdempotencyKey { get; }
    public int AttemptCount { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? StartedAt { get; }
    public DateTimeOffset? CompletedAt { get; }
    public string? ProviderReceipt { get; }
    public string? VerificationState { get; }
    public string? Failure { get; }
    public int SchemaVersion { get; }
    public long Version { get; }

    public static ActionRecord Create(
        string actionId,
        string ownerPrincipalId,
        string capabilityId,
        string capabilityVersion,
        string intent,
        string payloadHash,
        string targetScope,
        string riskClass,
        string policyDecisionRef,
        string? authorizationRef,
        ActionState state,
        string idempotencyKey,
        int attemptCount,
        DateTimeOffset createdAt,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt,
        string? providerReceipt,
        string? verificationState,
        string? failure,
        int schemaVersion,
        long version)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attemptCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(schemaVersion, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(version);
        ValidateState(
            state,
            authorizationRef,
            attemptCount,
            startedAt,
            completedAt,
            providerReceipt,
            verificationState);
        return new ActionRecord(
            KernelValidation.Text(actionId, nameof(actionId), 256),
            KernelValidation.Text(ownerPrincipalId, nameof(ownerPrincipalId), 256),
            KernelValidation.Text(capabilityId, nameof(capabilityId), 256),
            KernelValidation.Text(capabilityVersion, nameof(capabilityVersion), 64),
            KernelValidation.Text(intent, nameof(intent), 2048),
            KernelValidation.Text(payloadHash, nameof(payloadHash), 512),
            KernelValidation.Text(targetScope, nameof(targetScope), 1024),
            KernelValidation.Text(riskClass, nameof(riskClass), 128),
            KernelValidation.Text(policyDecisionRef, nameof(policyDecisionRef), 256),
            authorizationRef is null ? null : KernelValidation.Text(authorizationRef, nameof(authorizationRef), 256),
            state,
            KernelValidation.Text(idempotencyKey, nameof(idempotencyKey), 256),
            attemptCount,
            KernelValidation.Timestamp(createdAt, nameof(createdAt)),
            startedAt?.ToUniversalTime(),
            completedAt?.ToUniversalTime(),
            KernelValidation.PersistedNonSecretText(providerReceipt, nameof(providerReceipt), 4096),
            KernelValidation.PersistedNonSecretText(verificationState, nameof(verificationState), 2048),
            KernelValidation.PersistedNonSecretText(failure, nameof(failure), 2048),
            schemaVersion,
            version);
    }

    public ActionRecord BindR2(ActionR2Binding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (State != ActionState.Proposed || R2Binding is not null)
        {
            throw new InvalidOperationException("R2 bindings are immutable and must be set while proposed.");
        }

        return this with { R2Binding = binding };
    }

    public ActionRecord TransitionTo(
        ActionState next,
        DateTimeOffset at,
        string? authorizationRef = null,
        string? providerReceipt = null,
        string? verificationState = null,
        string? failure = null)
    {
        if (!CanTransition(State, next))
        {
            throw new InvalidOperationException($"Invalid action transition {State.ToContractValue()} -> {next.ToContractValue()}.");
        }

        var timestamp = KernelValidation.Timestamp(at, nameof(at));
        return Create(
            ActionId,
            OwnerPrincipalId,
            CapabilityId,
            CapabilityVersion,
            Intent,
            PayloadHash,
            TargetScope,
            RiskClass,
            PolicyDecisionRef,
            authorizationRef ?? AuthorizationRef,
            next,
            IdempotencyKey,
            AttemptCount + (next == ActionState.Started ? 1 : 0),
            CreatedAt,
            next == ActionState.Started ? timestamp : StartedAt,
            next is ActionState.ExternallyConfirmed or ActionState.Failed or ActionState.Canceled or ActionState.Expired ? timestamp : CompletedAt,
            providerReceipt ?? ProviderReceipt,
            verificationState ?? VerificationState,
            failure,
            SchemaVersion,
            Version + 1) with { R2Binding = R2Binding };
    }

    public static bool CanTransition(ActionState current, ActionState next) => (current, next) switch
    {
        (ActionState.Proposed, ActionState.Authorized or ActionState.Canceled or ActionState.Expired) => true,
        (ActionState.Authorized, ActionState.Started or ActionState.Canceled) => true,
        (ActionState.Started, ActionState.ExecutionSucceeded or ActionState.Failed or ActionState.ReconciliationRequired) => true,
        (ActionState.ExecutionSucceeded, ActionState.ProviderVerified or ActionState.Failed or ActionState.ReconciliationRequired) => true,
        (ActionState.ProviderVerified, ActionState.ExternallyConfirmed or ActionState.ReconciliationRequired) => true,
        (ActionState.Failed, ActionState.Started or ActionState.Canceled) => true,
        (ActionState.ReconciliationRequired, ActionState.Started or ActionState.ProviderVerified or ActionState.Failed or ActionState.Canceled) => true,
        _ => false,
    };

    private static void ValidateState(
        ActionState state,
        string? authorizationRef,
        int attemptCount,
        DateTimeOffset? startedAt,
        DateTimeOffset? completedAt,
        string? providerReceipt,
        string? verificationState)
    {
        if (state == ActionState.Proposed
            && (authorizationRef is not null || attemptCount != 0 || startedAt is not null || completedAt is not null))
        {
            throw new InvalidOperationException("A proposed action cannot already be authorized, attempted, or completed.");
        }

        if (state is not (ActionState.Proposed or ActionState.Canceled or ActionState.Expired)
            && string.IsNullOrWhiteSpace(authorizationRef))
        {
            throw new InvalidOperationException("An active action state requires an authorization reference.");
        }

        if (state is ActionState.Started or ActionState.ExecutionSucceeded or ActionState.ProviderVerified
                or ActionState.ExternallyConfirmed or ActionState.Failed or ActionState.ReconciliationRequired
            && (attemptCount < 1 || startedAt is null))
        {
            throw new InvalidOperationException("An executed action state requires a recorded attempt and start time.");
        }

        if (state is ActionState.ProviderVerified or ActionState.ExternallyConfirmed
            && (string.IsNullOrWhiteSpace(providerReceipt) || string.IsNullOrWhiteSpace(verificationState)))
        {
            throw new InvalidOperationException("A verified action requires provider receipt and verification metadata.");
        }

        if (state == ActionState.ExternallyConfirmed && completedAt is null)
        {
            throw new InvalidOperationException("External confirmation must record completion time.");
        }
    }
}

public sealed record ActionR2Binding(
    string? AccountId,
    string PluginId,
    string PluginVersion,
    string TargetHash,
    DateTimeOffset ExpiresAt,
    string ExecutionId,
    string? ConversationId = null,
    string? MessageId = null,
    string? JobId = null,
    string? JobRunId = null);

public static class ActionPayloadHash
{
    public const string Algorithm = "SHA-256";
    public const int Version = 1;

    public static string Compute(ReadOnlySpan<byte> canonicalPayload)
        => Convert.ToHexStringLower(SHA256.HashData(canonicalPayload));
}

public sealed record ActionAuthorization(
    string AuthorizationId,
    string OwnerPrincipalId,
    string CapabilityId,
    string CapabilityVersion,
    string ActionId,
    string PayloadHash,
    string TargetScope,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ConsumedAt,
    ActionR2Binding? R2Binding = null);

public sealed class ActionAuthorizationService(IActionAuthorizationRepository repository)
{
    private readonly IActionAuthorizationRepository _repository = repository
        ?? throw new ArgumentNullException(nameof(repository));

    public async Task<ActionAuthorization> IssueAsync(
        ActionRecord action,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
        =>await IssueAsync(action,Guid.NewGuid().ToString("N"),issuedAt,expiresAt,cancellationToken).ConfigureAwait(false);

    public async Task<ActionAuthorization> IssueAsync(
        ActionRecord action,string authorizationId,DateTimeOffset issuedAt,DateTimeOffset expiresAt,
        CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.State != ActionState.Proposed || expiresAt <= issuedAt)
        {
            throw new InvalidOperationException("Authorization requires a proposed action and a future expiry.");
        }

        var authorization = new ActionAuthorization(
            authorizationId,
            action.OwnerPrincipalId,
            action.CapabilityId,
            action.CapabilityVersion,
            action.ActionId,
            action.PayloadHash,
            action.TargetScope,
            issuedAt.ToUniversalTime(),
            expiresAt.ToUniversalTime(),
            null,
            action.R2Binding);
        await _repository.AddAsync(action.OwnerPrincipalId, authorization, cancellationToken).ConfigureAwait(false);
        return authorization;
    }

    public Task<ActionRecord?> AuthorizeAsync(
        ActionRecord proposedAction,
        string authorizationId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposedAction);
        if (proposedAction.State != ActionState.Proposed)
        {
            throw new InvalidOperationException("Only a proposed action can consume authorization.");
        }

        return _repository.TryConsumeAndAuthorizeAsync(
            proposedAction.OwnerPrincipalId,
            authorizationId,
            proposedAction,
            now.ToUniversalTime(),
            cancellationToken);
    }
}