using System.Text.Json;
using Tessera.Core.Kernel;

namespace Tessera.Core.Product;

public sealed record ExecutionRequest(
    string OwnerPrincipalId,
    string ExecutionId,
    string CapabilityId,
    string CapabilityVersion,
    string PluginId,
    string PluginVersion,
    string? AccountId,
    string TargetScope,
    string TargetHash,
    JsonElement Input,
    string IdempotencyKey,
    string? ConversationId = null,
    string? MessageId = null,
    string? JobId = null,
    string? JobRunId = null);

public sealed record ExecutionDecision(bool Available, string? BlockedCode = null);
public sealed record ExecutionResponse(ActionRecord? Action, CapabilityResult? Result, bool ApprovalRequired);

public interface ICapabilityAvailability
{
    ValueTask<ExecutionDecision> CheckAsync(ExecutionRequest request, CancellationToken cancellationToken = default);
}

public interface IDurableExecutionRequestRepository
{
    Task AddProposedAsync(
        string ownerPrincipalId,
        ActionRecord action,
        ExecutionRequest request,
        CancellationToken cancellationToken = default);

    Task<ExecutionRequest?> GetAsync(
        string ownerPrincipalId,
        string actionId,
        CancellationToken cancellationToken = default);

    Task<(ActionRecord Action,ExecutionRequest Request)?> GetByIdempotencyAsync(
        string ownerPrincipalId,string idempotencyKey,CancellationToken cancellationToken=default);
}

public sealed class ExecutionCoordinator(
    CapabilityRegistry capabilities,
    IActionRepository actions,
    IActionAuthorizationRepository authorizations,
    IActionExecutionRepository executions,
    ICapabilityAvailability availability,
    IDurableExecutionRequestRepository durableRequests,
    ICapabilityTraceRepository? traceRepository=null)
{
    private readonly ICapabilityTraceRepository? _traceRepository = traceRepository ?? availability as ICapabilityTraceRepository;

    public async Task<ExecutionResponse> ExecuteOrProposeAsync(
        ExecutionRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ProductContentValidation.Text(request.TargetScope, nameof(request.TargetScope), 2048);
        ProductContentValidation.Json(request.Input, nameof(request.Input));
        var replay=await durableRequests.GetByIdempotencyAsync(request.OwnerPrincipalId,request.IdempotencyKey,cancellationToken).ConfigureAwait(false);
        if(replay is not null)
        {
            if(!Equivalent(replay.Value.Request,request))throw new ProductConcurrencyException("Idempotency key was already used for a different Action request.");
            return new(replay.Value.Action,null,true);
        }
        if(_traceRepository is not null)await _traceRepository.BeginCapabilityCallAsync(request,now,cancellationToken).ConfigureAwait(false);
        var capability = capabilities.Resolve(request.CapabilityId, request.CapabilityVersion);
        await RequireAvailableAsync(request, cancellationToken).ConfigureAwait(false);
        if (capability.Descriptor.SideEffectClass == SideEffectClass.ReadOnly)
        {
            if(_traceRepository is not null&&await _traceRepository.GetCompletedCapabilityResultAsync(request,cancellationToken).ConfigureAwait(false) is { } completed)
                return new(null,completed,false);
            if(_traceRepository is not null&&!await _traceRepository.TryStartCapabilityCallAsync(request,DateTimeOffset.UtcNow,cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("capability_dependency_changed");
            var result = await capability.InvokeAsync(ToInvocation(request, null), cancellationToken).ConfigureAwait(false);
            result=SafeResult(result);if(_traceRepository is not null)await _traceRepository.CompleteCapabilityCallAsync(request,result,DateTimeOffset.UtcNow,cancellationToken).ConfigureAwait(false);
            return new(null, result, false);
        }

        var action = ActionRecord.Create(
            Guid.NewGuid().ToString("N"), request.OwnerPrincipalId, request.CapabilityId, request.CapabilityVersion,
            "capability invocation", CapabilityPayloadHash.Compute(request.Input), request.TargetScope,
            capability.Descriptor.SideEffectClass.ToString(), "execution-coordinator", null, ActionState.Proposed,
            request.IdempotencyKey, 0, now, null, null, null, null, null, 2, 0).BindR2(new(
                request.AccountId, request.PluginId, request.PluginVersion, request.TargetHash, now.AddMinutes(10),
                request.ExecutionId, request.ConversationId, request.MessageId, request.JobId, request.JobRunId));
        await durableRequests.AddProposedAsync(request.OwnerPrincipalId, action, request, cancellationToken).ConfigureAwait(false);
        return new(action, null, true);
    }

    public async Task<ExecutionResponse> ApproveAndExecuteAsync(
        string ownerPrincipalId,
        string actionId,
        long expectedVersion,
        string idempotencyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        ValidateIdempotencyKey(idempotencyKey);
        var authorizationId=ApprovalAuthorizationId(ownerPrincipalId,actionId,idempotencyKey);
        var action = await actions.GetAsync(ownerPrincipalId, actionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Action not found.");
        if (action.Version != expectedVersion)
        {
            var receipt=await authorizations.GetAsync(ownerPrincipalId,authorizationId,cancellationToken).ConfigureAwait(false);
            if(receipt?.ActionId==actionId&&receipt.ConsumedAt is not null&&action.State is ActionState.ExecutionSucceeded or ActionState.ProviderVerified or ActionState.ExternallyConfirmed or ActionState.Failed or ActionState.ReconciliationRequired or ActionState.Canceled)
                return new(action,null,false);
            throw new ProductConcurrencyException("Action version changed before approval.");
        }

        var request = await durableRequests.GetAsync(ownerPrincipalId, actionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The durable Action request is missing.");
        return await ApproveAndExecuteCoreAsync(request,actionId,now,authorizationId,cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExecutionResponse> ApproveAndExecuteAsync(
        ExecutionRequest request,
        string actionId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
        =>await ApproveAndExecuteCoreAsync(request,actionId,now,null,cancellationToken).ConfigureAwait(false);

    private async Task<ExecutionResponse> ApproveAndExecuteCoreAsync(
        ExecutionRequest request,string actionId,DateTimeOffset now,string? authorizationId,CancellationToken cancellationToken)
    {
        var proposed = await actions.GetAsync(request.OwnerPrincipalId, actionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Action not found.");
        ProductContentValidation.Json(request.Input, nameof(request.Input));
        if(_traceRepository is not null)await _traceRepository.BeginCapabilityCallAsync(request,now,cancellationToken).ConfigureAwait(false);
        EnsureExact(proposed, request, now);
        await RequireAvailableAsync(request, cancellationToken).ConfigureAwait(false);
        var authorizationService = new ActionAuthorizationService(authorizations);
                var authorization = authorizationId is null
                        ?await authorizationService.IssueAsync(proposed,now,proposed.R2Binding!.ExpiresAt,cancellationToken).ConfigureAwait(false)
                        :await authorizations.GetAsync(request.OwnerPrincipalId,authorizationId,cancellationToken).ConfigureAwait(false)
                            ??await authorizationService.IssueAsync(proposed,authorizationId,now,proposed.R2Binding!.ExpiresAt,cancellationToken).ConfigureAwait(false);
        var authorized = await authorizationService.AuthorizeAsync(proposed, authorization.AuthorizationId, now, cancellationToken).ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("Action authorization was already consumed or no longer exact.");
        await RequireAvailableAsync(request, cancellationToken).ConfigureAwait(false);
        var capability = capabilities.Resolve(request.CapabilityId, request.CapabilityVersion);
        CapabilityResult result;
        try
        {
            result = await new ActionExecutionService(executions).InvokeAsync(
                actionId, authorized.Version, capability, ToInvocation(request, authorization.AuthorizationId), now,
                cancellationToken).ConfigureAwait(false);
            result = SafeResult(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var reconciliation=await ReconcileIfStartedAsync(request,actionId,"provider_canceled_outcome_unknown",CancellationToken.None).ConfigureAwait(false);if(reconciliation is not null)return reconciliation;throw;
        }
        catch (Exception)
        {
            var reconciliation=await ReconcileIfStartedAsync(request,actionId,"provider_unknown_exception",cancellationToken).ConfigureAwait(false);if(reconciliation is not null)return reconciliation;throw;
        }
        var started = await actions.GetAsync(request.OwnerPrincipalId, actionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Started Action is missing.");
        var next = result.Outcome switch
        {
            CapabilityOutcome.Succeeded => started.TransitionTo(ActionState.ExecutionSucceeded, DateTimeOffset.UtcNow, providerReceipt: result.ProviderReceipt),
            CapabilityOutcome.UnknownOutcome => started.TransitionTo(ActionState.ReconciliationRequired, DateTimeOffset.UtcNow, failure: result.FailureCode),
            _ => started.TransitionTo(ActionState.Failed, DateTimeOffset.UtcNow, failure: result.FailureCode),
        };
        if (!await actions.UpdateAsync(request.OwnerPrincipalId, next, started.Version, cancellationToken).ConfigureAwait(false))
            throw new ProductConcurrencyException("Action changed while recording execution outcome.");
        if (result.Outcome == CapabilityOutcome.Succeeded && result.VerificationMetadata is not null && result.ProviderReceipt is not null)
        {
            var verified = next.TransitionTo(ActionState.ProviderVerified, DateTimeOffset.UtcNow,
                providerReceipt: result.ProviderReceipt, verificationState: result.VerificationMetadata);
            if (!await actions.UpdateAsync(request.OwnerPrincipalId, verified, next.Version, cancellationToken).ConfigureAwait(false))
                throw new ProductConcurrencyException("Action changed while recording verification.");
            next = verified.TransitionTo(ActionState.ExternallyConfirmed, DateTimeOffset.UtcNow);
            if (!await actions.UpdateAsync(request.OwnerPrincipalId, next, verified.Version, cancellationToken).ConfigureAwait(false))
                throw new ProductConcurrencyException("Action changed while confirming external outcome.");
        }
        return new(next,await TraceAsync(request,result,DateTimeOffset.UtcNow,cancellationToken).ConfigureAwait(false),false);
    }

    private async Task<CapabilityResult> TraceAsync(ExecutionRequest request,CapabilityResult result,DateTimeOffset now,CancellationToken token)
    {if(_traceRepository is not null)await _traceRepository.CompleteCapabilityCallAsync(request,result,now,token).ConfigureAwait(false);return result;}

    private async Task<ExecutionResponse?> ReconcileIfStartedAsync(ExecutionRequest request,string actionId,string failure,CancellationToken token)
    {var indeterminate=await actions.GetAsync(request.OwnerPrincipalId,actionId,token).ConfigureAwait(false)??throw new InvalidDataException("Action is missing after capability failure.");if(indeterminate.State!=ActionState.Started)return null;var reconciliation=indeterminate.TransitionTo(ActionState.ReconciliationRequired,DateTimeOffset.UtcNow,failure:failure);if(!await actions.UpdateAsync(request.OwnerPrincipalId,reconciliation,indeterminate.Version,token).ConfigureAwait(false))throw new ProductConcurrencyException("Action changed while recording unknown provider outcome.");var result=new CapabilityResult(CapabilityOutcome.UnknownOutcome,JsonSerializer.SerializeToElement(new{}),null,null,failure);return new(reconciliation,await TraceAsync(request,result,DateTimeOffset.UtcNow,token).ConfigureAwait(false),false);}

    private static CapabilityResult SafeResult(CapabilityResult result)
    {
        if (result.Outcome != CapabilityOutcome.Succeeded) return result;
        try
        {
            ProductContentValidation.Json(result.Output, nameof(result.Output));
            if (result.ProviderReceipt is not null)
                ProductContentValidation.Text(result.ProviderReceipt, nameof(result.ProviderReceipt), 2048);
            if (result.VerificationMetadata is not null)
                ProductContentValidation.Text(result.VerificationMetadata, nameof(result.VerificationMetadata), 2048);
            return result;
        }
        catch (ArgumentException)
        {
            return new(result.ProviderReceipt is null ? CapabilityOutcome.Failed : CapabilityOutcome.UnknownOutcome,
                JsonSerializer.SerializeToElement(new { }), null, null, "provider_unsafe_content", result.RuntimeIdentity);
        }
    }

    private async Task RequireAvailableAsync(ExecutionRequest request, CancellationToken cancellationToken)
    {
        var decision = await availability.CheckAsync(request, cancellationToken).ConfigureAwait(false);
        if (!decision.Available) throw new InvalidOperationException(decision.BlockedCode ?? "capability_unavailable");
    }

    private static CapabilityInvocation ToInvocation(ExecutionRequest request, string? authorizationId)
        => new(request.OwnerPrincipalId, request.ExecutionId, request.CapabilityId, request.CapabilityVersion,
            request.TargetScope, request.Input, authorizationId, request.IdempotencyKey);

    private static void EnsureExact(ActionRecord action, ExecutionRequest request, DateTimeOffset now)
    {
        var binding = action.R2Binding;
        if (action.State != ActionState.Proposed || binding is null || binding.ExpiresAt <= now
            || action.PayloadHash != CapabilityPayloadHash.Compute(request.Input)
            || action.TargetScope != request.TargetScope || binding.TargetHash != request.TargetHash
            || binding.AccountId != request.AccountId || binding.PluginId != request.PluginId
            || binding.PluginVersion != request.PluginVersion || binding.ExecutionId != request.ExecutionId)
            throw new UnauthorizedAccessException("Approval request does not exactly match the durable Action.");
    }

    private static void ValidateIdempotencyKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(character => character is < '!' or > '~'))
        {
            throw new ArgumentException("Idempotency key must contain 1-128 visible ASCII characters.", nameof(value));
        }
    }

    private static bool Equivalent(ExecutionRequest left,ExecutionRequest right)
        =>left.CapabilityId==right.CapabilityId&&left.CapabilityVersion==right.CapabilityVersion
          &&left.PluginId==right.PluginId&&left.PluginVersion==right.PluginVersion&&left.AccountId==right.AccountId
          &&left.TargetScope==right.TargetScope&&left.TargetHash==right.TargetHash
          &&CapabilityPayloadHash.Compute(left.Input)==CapabilityPayloadHash.Compute(right.Input)
          &&left.ConversationId==right.ConversationId&&left.MessageId==right.MessageId
          &&left.JobId==right.JobId&&left.JobRunId==right.JobRunId;

        private static string ApprovalAuthorizationId(string owner,string actionId,string key)
                =>ActionPayloadHash.Compute(System.Text.Encoding.UTF8.GetBytes($"{owner}\n{actionId}\n{key}"));
}