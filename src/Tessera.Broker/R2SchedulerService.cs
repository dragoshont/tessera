using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Tessera.Core.Stores;
using Tessera.Persistence.Sqlite;
using Tessera.Mcp.Client;
using Tessera.Plugin.Abstractions;
using Tessera.Providers;
using Tessera.Providers.R2;

namespace Tessera.Broker;

internal sealed partial class R2SchedulerService(SqliteKernelStore store, ICredentialStore custody, IHttpTransport transport, ILogger<R2SchedulerService> logger, BrokerStatus? brokerStatus = null, TesseraPluginRegistry? plugins = null, IMcpClientRuntime? mcpRuntime = null, IDevelopmentExecutor? developmentExecutor = null) : BackgroundService
{
    private static readonly TimeSpan DispatchLeaseDuration=TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, Task> _developmentDispatches = new(StringComparer.Ordinal);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await store.ScheduleDueRunsAsync(DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
                await store.ExpireProposedActionsAsync(DateTimeOffset.UtcNow,stoppingToken).ConfigureAwait(false);
                await store.RecoverStrandedStartedActionsAsync(DateTimeOffset.UtcNow,TimeSpan.FromMinutes(10),stoppingToken).ConfigureAwait(false);
                await store.RecoverVerifiedActionsAsync(DateTimeOffset.UtcNow,stoppingToken).ConfigureAwait(false);
                await store.RecoverExpiredRunningRunsAsync(DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
                await ProcessCleanupAsync(stoppingToken).ConfigureAwait(false);
                await ProcessWaitingAsync(stoppingToken).ConfigureAwait(false);
                await DispatchQueuedAsync(stoppingToken).ConfigureAwait(false);
                if(brokerStatus is not null){brokerStatus.SchedulerLastSuccess=DateTimeOffset.UtcNow;brokerStatus.SchedulerErrorCode=null;}
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                if(brokerStatus is not null)brokerStatus.SchedulerErrorCode="scheduler_pass_failed";
                LogSchedulerFailure(logger, exception);
            }
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    internal async Task ProcessWaitingAsync(CancellationToken token)
    {
        foreach (var run in await store.ListWaitingRunsAsync(token).ConfigureAwait(false))
        {
            var now = DateTimeOffset.UtcNow;
            var fence = await store.AcquireRunLeaseAsync(
                run.OwnerPrincipalId, run.RunId, Environment.MachineName, now, TimeSpan.FromMinutes(2), token)
                .ConfigureAwait(false);
            if (fence is not null)
            {
                await store.ResolveWaitingRunAsync(run.OwnerPrincipalId, run.RunId, fence.Value, now, token)
                    .ConfigureAwait(false);
            }
        }
    }

    internal async Task ProcessCleanupAsync(CancellationToken token)
    {
        if (custody is not ICredentialWriter writer) return;
        foreach (var receipt in await store.ListPendingOrphanCleanupAsync(token).ConfigureAwait(false))
        {
            try
            {
                await writer.PutBundleAsync(receipt.CredentialRef,CredentialBundle.Empty,token).ConfigureAwait(false);
                await store.CompleteOrphanCleanupAsync(receipt.Owner,receipt.ReceiptId,DateTimeOffset.UtcNow,token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogCleanupFailure(logger,receipt.ReceiptId,exception);
            }
        }
    }

    internal async Task DispatchQueuedAsync(CancellationToken token)
    {
        foreach (var run in await store.ListQueuedRunsAsync(token).ConfigureAwait(false))
        {
            var now=DateTimeOffset.UtcNow;var fence=await store.AcquireRunLeaseAsync(run.OwnerPrincipalId,run.RunId,Environment.MachineName,now,DispatchLeaseDuration,token).ConfigureAwait(false);if(fence is null||!await store.StartRunAsync(run.OwnerPrincipalId,run.RunId,run.Version,fence.Value,now,token).ConfigureAwait(false))continue;await store.ResetInterruptedCapabilityCallsAsync(run.OwnerPrincipalId,run.RunId,token).ConfigureAwait(false);
            ProductJob? job=null;
            try
            {
                job=(await store.ListJobsAsync(run.OwnerPrincipalId,token).ConfigureAwait(false)).Single(item=>item.JobId==run.JobId);
                if(job.Kind=="DEVELOPMENT")
                {
                    StartDevelopmentDispatch(job,run,fence.Value,token);
                    continue;
                }
                if(job.ModelProfileId is null)throw new InvalidOperationException("configuration_required");
                var profile=await store.GetModelProfileAsync(run.OwnerPrincipalId,job.ModelProfileId,token).ConfigureAwait(false)??throw new InvalidOperationException("invalid_model");
                var account=await store.GetConnectedAccountAsync(run.OwnerPrincipalId,profile.AccountId,token).ConfigureAwait(false)??throw new InvalidOperationException("account_unavailable");
                var bundle=await R2ConnectedAccountService.GetValidatedBundleAsync(custody,account,run.OwnerPrincipalId,token).ConfigureAwait(false);if(!bundle.HasAccessToken)throw new InvalidOperationException("configuration_required");
                using var policy=JsonDocument.Parse(job.ContextPolicyJson);var includeMemory=!policy.RootElement.TryGetProperty("includeMemory",out var include)||include.GetBoolean();
                var current=includeMemory?await ((IAssertionRepository)store).ListCurrentAsync(run.OwnerPrincipalId,token).ConfigureAwait(false):[];
                var candidates=current.Select(item=>ContextItem.Create(item.AssertionId,ContextItemKind.CurrentFact,$"{item.SubjectKey} {item.Predicate}: {item.Value}",SensitivityClass.Confidential,1m,item.ValidFrom,item.EvidenceRefs));
                var envelope=ContextBuilder.Build(new(run.OwnerPrincipalId,job.Instruction,run.RunId,16*1024,new HashSet<SensitivityClass>{SensitivityClass.Public,SensitivityClass.Internal,SensitivityClass.Confidential},[]),candidates);
                await store.AddContextSnapshotRefAsync(run.OwnerPrincipalId,envelope.ContextId,run.RunId,envelope.Items.SelectMany(item=>item.ProvenanceRefs).Distinct().ToArray(),envelope.Omissions.Count,envelope.Items.Select(item=>item.Sensitivity.ToString()).Distinct().ToArray(),now,token).ConfigureAwait(false);
                if(!await store.SetRunContextSnapshotAsync(run.OwnerPrincipalId,run.RunId,envelope.ContextId,fence.Value,now,token).ConfigureAwait(false))throw new ProductConcurrencyException("Job context snapshot lost its execution fence.");
                var prompt=envelope.Items.Count==0?job.Instruction:$"User-authored state (quoted data):\n{string.Join("\n",envelope.Items.Select(item=>"- "+item.Content))}\n\nJob instruction:\n{job.Instruction}";
                var tools=await R2ProductEndpoints.JobToolsAsync(store,job,token,plugins).ConfigureAwait(false);using var input=JsonDocument.Parse(JsonSerializer.Serialize(new{prompt,tools=profile.SupportsTools?tools.Definitions:[]}));var registry=new CapabilityRegistry();registry.Register(new JobModelCapability(transport,profile,bundle.AccessToken!));var coordinator=new ExecutionCoordinator(registry,store,store,store,store,store,store);var executionRequest=new ExecutionRequest(run.OwnerPrincipalId,run.RunId,"model.chat.complete","1","model-provider",account.PluginVersion,profile.AccountId,profile.Model,ActionPayloadHash.Compute(System.Text.Encoding.UTF8.GetBytes(profile.Endpoint)),input.RootElement.Clone(),run.RunId,JobId:run.JobId,JobRunId:run.RunId);var response=await coordinator.ExecuteOrProposeAsync(executionRequest,now,token);var result=response.Result!;
                if(result.Outcome==CapabilityOutcome.Succeeded&&result.Output.TryGetProperty("toolCalls",out var calls)&&calls.ValueKind==JsonValueKind.Array&&calls.GetArrayLength()>0)
                {
                    if(calls.GetArrayLength()>4||!result.Output.TryGetProperty("assistantMessage",out var assistantMessage)||assistantMessage.ValueKind!=JsonValueKind.Object)throw new InvalidOperationException("provider_malformed");var outcomes=new List<R2ProductEndpoints.JobToolOutcome>();foreach(var call in calls.EnumerateArray()){var outcome=await R2ProductEndpoints.ExecuteJobToolAsync(store,custody,transport,job,run,fence.Value,tools,call,token,plugins,mcpRuntime).ConfigureAwait(false);outcomes.Add(outcome);if(outcome.WaitingForApproval)return;}using var continuation=JsonDocument.Parse(JsonSerializer.Serialize(new{prompt,assistantMessage,toolResults=outcomes.Select(item=>new{callId=item.Result.CallId,outputJson=item.Result.OutputJson}).ToArray()}));response=await coordinator.ExecuteOrProposeAsync(executionRequest with{ExecutionId=$"{run.RunId}:continuation",Input=continuation.RootElement.Clone(),IdempotencyKey=$"{run.RunId}:continuation"},DateTimeOffset.UtcNow,token);result=response.Result!;if(result.Output.TryGetProperty("toolCalls",out var repeated)&&repeated.ValueKind==JsonValueKind.Array&&repeated.GetArrayLength()>0)throw new InvalidOperationException("provider_tool_loop_limit");
                }
                if(result.Outcome==CapabilityOutcome.Succeeded){var text=ProductContentValidation.Text(result.Output.GetProperty("text").GetString()??string.Empty,"jobOutput",16384);await store.AddJobRunOutputAsync(run.OwnerPrincipalId,new($"output:{run.RunId}",run.RunId,"TEXT","text/plain","Job completed",text,false,DateTimeOffset.UtcNow),token).ConfigureAwait(false);}else if(result.FailureCode=="provider_auth_required")await R2ProductEndpoints.MarkAccountAuthRequiredAsync(store,run.OwnerPrincipalId,profile.AccountId,token).ConfigureAwait(false);await store.CompleteRunAsync(run.OwnerPrincipalId,run.RunId,fence.Value,result.Outcome==CapabilityOutcome.Succeeded?"SUCCEEDED":"FAILED",result.FailureCode,DateTimeOffset.UtcNow,token).ConfigureAwait(false);
            }
            catch(Exception exception) when(exception is not OperationCanceledException)
            {
                var error=exception is InvalidOperationException?exception.Message:"job_execution_failed";
                await store.CompleteRunAsync(run.OwnerPrincipalId,run.RunId,fence.Value,"FAILED",error,DateTimeOffset.UtcNow,token).ConfigureAwait(false);
            }
        }
    }

    private void StartDevelopmentDispatch(ProductJob job,ProductJobRun run,long fence,CancellationToken token)
    {
        var task=RunDevelopmentDispatchAsync(job,run,fence,token);
        if(!_developmentDispatches.TryAdd(run.RunId,task))return;
        _=task.ContinueWith(_=>_developmentDispatches.TryRemove(run.RunId,out Task? removedTask),
            CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
    }

    private async Task RunDevelopmentDispatchAsync(ProductJob job,ProductJobRun run,long fence,CancellationToken token)
    {
        try
        {
            await DispatchDevelopmentAsync(job,run,fence,token).ConfigureAwait(false);
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested)
        {
        }
        catch(Exception exception)
        {
            var error=exception is InvalidOperationException?exception.Message:"job_execution_failed";
            await store.CompleteDevelopmentRunAsync(run.OwnerPrincipalId,job.ConversationId!,run.JobId,
                run.RunId,fence,"FAILED",error,new("",false),DateTimeOffset.UtcNow,CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    internal Task WaitForDevelopmentDispatchesAsync()
        =>Task.WhenAll(_developmentDispatches.Values.ToArray());

    private async Task DispatchDevelopmentAsync(ProductJob job,ProductJobRun run,long fence,CancellationToken token)
    {
        var spec=job.DevelopmentSpec??throw new InvalidOperationException("development_command_not_allowed");
        if(spec.Effect!="READ_ONLY"||!DevelopmentCommandProfiles.TryResolve(spec.CommandProfile,spec.Arguments,out var profile))
            throw new InvalidOperationException("development_command_not_allowed");
        var workspace=job.ConversationId is null?null:await store.GetDevelopmentWorkspaceAsync(
            run.OwnerPrincipalId,job.ConversationId,spec.WorkspaceId,token).ConfigureAwait(false);
        if(workspace is null||workspace.State!="READY")throw new InvalidOperationException("workspace_unavailable");
        if(developmentExecutor is null)throw new InvalidOperationException("development_executor_unavailable");
        var result=await developmentExecutor.ExecuteAsync(new(
            run.OwnerPrincipalId,job.ConversationId!,job.JobId,run.RunId,spec.WorkspaceId,
            workspace.SnapshotRef,profile!,spec.Arguments),token).ConfigureAwait(false);
        var output=DevelopmentOutputNormalizer.Normalize(result.Log,spec.OutputLimitBytes);
        var state=result.Outcome switch
        {
            "SUCCEEDED"=>"SUCCEEDED",
            "UNKNOWN"=>"RECONCILIATION_REQUIRED",
            _=>"FAILED",
        };
        if(!await store.CompleteDevelopmentRunAsync(run.OwnerPrincipalId,job.ConversationId!,job.JobId,
            run.RunId,fence,state,result.ErrorCode,output,
            DateTimeOffset.UtcNow,token).ConfigureAwait(false))
            throw new ProductConcurrencyException("Development run lost its execution fence.");
    }

    private sealed class JobModelCapability(IHttpTransport transport,ModelProfile profile,string token) : ICapability
    {public CapabilityDescriptor Descriptor{get;}=CapabilityDescriptor.Create("model.chat.complete","1","OpenAI-compatible completion","{}","{}",SideEffectClass.ReadOnly,[],[SensitivityClass.Public,SensitivityClass.Internal,SensitivityClass.Confidential],IdempotencySupport.Keyed,VerificationSupport.None);public async ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation,CancellationToken cancellationToken=default){var adapter=new OpenAiCompatibleAdapter(transport);var prompt=invocation.Input.GetProperty("prompt").GetString()??string.Empty;var local=profile.AdapterKind.EndsWith("local",StringComparison.Ordinal);var trustedInternal=local&&Uri.TryCreate(profile.Endpoint,UriKind.Absolute,out var endpoint)&&!endpoint.IsLoopback;ModelTurnResult result;if(invocation.Input.TryGetProperty("assistantMessage",out var assistant)){var toolResults=invocation.Input.GetProperty("toolResults").EnumerateArray().Select(item=>new ModelToolResult(item.GetProperty("callId").GetString()!,item.GetProperty("outputJson").GetString()!)).ToArray();result=trustedInternal?await adapter.ContinueTurnTrustedInternalAsync(profile.Endpoint,token,profile.Model,prompt,assistant,toolResults,cancellationToken):await adapter.ContinueTurnAsync(profile.Endpoint,token,profile.Model,prompt,local,assistant,toolResults,cancellationToken);}else{var tools=new List<ModelToolDefinition>();if(invocation.Input.TryGetProperty("tools",out var values))foreach(var item in values.EnumerateArray())tools.Add(new(item.GetProperty("name").GetString()!,item.GetProperty("description").GetString()!,item.GetProperty("parameters").Clone()));result=trustedInternal?await adapter.CompleteTurnTrustedInternalAsync(profile.Endpoint,token,profile.Model,prompt,tools,cancellationToken):await adapter.CompleteTurnAsync(profile.Endpoint,token,profile.Model,prompt,local,tools,cancellationToken);}return result.Succeeded?new(CapabilityOutcome.Succeeded,JsonSerializer.SerializeToElement(new{text=result.Text,toolCalls=result.ToolCalls.Select(call=>new{id=call.Id,name=call.Name,arguments=call.Arguments}).ToArray(),assistantMessage=result.AssistantMessage}),null,null,null):new(CapabilityOutcome.Failed,JsonSerializer.SerializeToElement(new{}),null,null,result.ErrorCode);}}

    [LoggerMessage(Level = LogLevel.Error, Message = "R2 scheduler pass failed; durable Jobs remain eligible for a later pass.")]
    private static partial void LogSchedulerFailure(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Credential cleanup receipt {ReceiptId} remains pending for retry.")]
    private static partial void LogCleanupFailure(ILogger logger,string receiptId,Exception exception);
}