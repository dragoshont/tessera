using System.Text.Json;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Xunit;

namespace Tessera.Persistence.Sqlite.Tests;

public sealed class ExecutionCoordinatorTests
{
    [Fact]
    public async Task Approval_reconstructs_the_exact_durable_request_after_restart()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var principal = PrincipalRef.Create("https://issuer.example", "tenant", "subject", "owner", now);
        await store.AddAsync(principal);
        await SeedGitHubDependencies(store,principal,now);
        var capability = new RecordingCapability();
        var registry = new CapabilityRegistry();
        registry.Register(capability);
        var proposal = await Coordinator(store, registry).ExecuteOrProposeAsync(
            Request(principal.PrincipalId, "target-a"), now);
        var replay=await Coordinator(store,registry).ExecuteOrProposeAsync(Request(principal.PrincipalId,"target-a"),now.AddMilliseconds(1));
        Assert.Equal(proposal.Action!.ActionId,replay.Action!.ActionId);
        await Assert.ThrowsAsync<ProductConcurrencyException>(()=>Coordinator(store,registry).ExecuteOrProposeAsync(Request(principal.PrincipalId,"different-target-hash"),now.AddMilliseconds(2)));

        var restarted = database.CreateStore();
        await restarted.InitializeAsync();
        var completed = await Coordinator(restarted, registry).ApproveAndExecuteAsync(
            principal.PrincipalId, proposal.Action!.ActionId, proposal.Action.Version,
            "approval-idempotency-1", now.AddSeconds(1));

        Assert.Equal(ActionState.ExternallyConfirmed, completed.Action!.State);
        Assert.Equal("owner/repo", capability.LastInvocation!.TargetScope);
        Assert.Equal("Safe issue", capability.LastInvocation.Input.GetProperty("title").GetString());
        var approvalReplay=await Coordinator(restarted,registry).ApproveAndExecuteAsync(principal.PrincipalId,proposal.Action.ActionId,proposal.Action.Version,"approval-idempotency-1",now.AddSeconds(2));
        Assert.Equal(completed.Action.ActionId,approvalReplay.Action!.ActionId);
        await Assert.ThrowsAsync<ProductConcurrencyException>(() => Coordinator(restarted, registry).ApproveAndExecuteAsync(
            principal.PrincipalId, proposal.Action.ActionId, proposal.Action.Version,
            "approval-idempotency-2", now.AddSeconds(3)));
    }

    [Fact]
    public async Task Exact_side_effect_survives_restart_and_rejects_substitution_and_replay()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore(); await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var principal = PrincipalRef.Create("https://issuer.example", "tenant", "subject", "owner", now);
        await store.AddAsync(principal);
        await SeedGitHubDependencies(store,principal,now);
        var registry = Registry();
        var request = Request(principal.PrincipalId, "target-a");
        var coordinator = Coordinator(store, registry);
        var proposal = await coordinator.ExecuteOrProposeAsync(request, now);
        Assert.True(proposal.ApprovalRequired);

        var restarted = database.CreateStore(); await restarted.InitializeAsync();
        var afterRestart = Coordinator(restarted, registry);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => afterRestart.ApproveAndExecuteAsync(
            Request(principal.PrincipalId, "target-b"), proposal.Action!.ActionId, now.AddSeconds(1)));

        var completed = await afterRestart.ApproveAndExecuteAsync(request, proposal.Action!.ActionId, now.AddSeconds(1));
        Assert.Equal(ActionState.ExternallyConfirmed, completed.Action!.State);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => afterRestart.ApproveAndExecuteAsync(
            request, proposal.Action.ActionId, now.AddSeconds(2)));
    }

    [Fact]
    public async Task Expired_durable_approval_fails_without_dispatch()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var principal = PrincipalRef.Create("https://issuer.example", "tenant", "subject", "owner", now);
        await store.AddAsync(principal);
        await SeedGitHubDependencies(store,principal,now);
        var capability = new RecordingCapability();
        var registry = new CapabilityRegistry();
        registry.Register(capability);
        var proposal = await Coordinator(store, registry).ExecuteOrProposeAsync(Request(principal.PrincipalId, "target-a"), now);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Coordinator(store, registry).ApproveAndExecuteAsync(
            principal.PrincipalId, proposal.Action!.ActionId, proposal.Action.Version,
            "expired-approval", now.AddMinutes(11)));

        Assert.Null(capability.LastInvocation);
    }

    [Fact]
    public async Task Expiry_sweep_materializes_legal_expired_action_state()
    {using var database=new TemporaryDatabase();var store=database.CreateStore();await store.InitializeAsync();var now=DateTimeOffset.UtcNow;var principal=PrincipalRef.Create("https://issuer.example","tenant","subject","owner",now);await store.AddAsync(principal);await SeedGitHubDependencies(store,principal,now);var proposal=await Coordinator(store,Registry()).ExecuteOrProposeAsync(Request(principal.PrincipalId,"target-a"),now);Assert.Equal(1,await store.ExpireProposedActionsAsync(now.AddMinutes(11)));var expired=await store.GetActionAsync(principal.PrincipalId,proposal.Action!.ActionId);Assert.Equal(ActionState.Expired,expired!.State);Assert.Equal("EXPIRED",expired.State.ToContractValue());}

    [Theory]
    [InlineData("plugin")]
    [InlineData("account")]
    [InlineData("grant")]
    [InlineData("effect")]
    [InlineData("job")]
    public async Task Dependency_change_immediately_before_approved_dispatch_fails_closed(string dependency)
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var principal = PrincipalRef.Create("https://issuer.example", "tenant", "subject", "owner", now);
        await store.AddAsync(principal);
        await store.AddPluginInstallationAsync(new(principal.PrincipalId, "github", "1.0.0", "GitHub", "Tessera",
            "hash", GitHubManifest, "{}", true, now, now, 1));
        await store.AddConnectedAccountAsync(new(principal.PrincipalId, "account-1", "github", "github", "1.0.0",
            "GitHub", null, AccountLifecycle.Connected, ConnectedAccountCredentialRef.Create(principal.PrincipalId,"account-1"), AccountHealth.Healthy, null,
            "{}", ["issues:write"], [new("github", "1.0.0", "github.issues.create", "1")], now, now, 1));
        var schedule = new JobSchedule("once", now, null, "UTC", null);
        await store.AddJobAsync(new(principal.PrincipalId, "job-1", "Job", "Create issue", "ACTIVE", "READY", null,
            schedule, null, "{}", ["account-1"], [("github.issues.create", "1")], ["ExternalCommunication"], now, now, 1));
        var capability = new RecordingCapability();
        var registry = new CapabilityRegistry();
        registry.Register(capability);
        using var input = JsonDocument.Parse("{\"title\":\"Safe issue\"}");
        var request = new ExecutionRequest(principal.PrincipalId, "run-1", "github.issues.create", "1", "github", "1.0.0",
            "account-1", "owner/repo", "target-a", input.RootElement.Clone(), "idem-job", JobId: "job-1", JobRunId: "run-1");
        var coordinator = new ExecutionCoordinator(registry, store, store, store, store, store);
        var proposal = await coordinator.ExecuteOrProposeAsync(request, now);

        if (dependency == "plugin")
            Assert.True(await store.SetPluginEnabledAsync(principal.PrincipalId, "github", "1.0.0", 1, false));
        else if (dependency == "account")
            await store.SetConnectedAccountStateAsync(principal.PrincipalId, "account-1", 1, AccountLifecycle.Disabled, AccountHealth.Unknown);
        else
        {
            var job = await store.GetJobAsync(principal.PrincipalId, "job-1");
            Assert.NotNull(job);
            var updated = dependency switch
            {
                "grant" => job with { CapabilityGrants = [] },
                "effect" => job with { SideEffectGrants = [] },
                "job" => job with { DesiredState = "PAUSED" },
                _ => job,
            };
            Assert.NotNull(await store.UpdateJobAsync(updated, job.Version));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ApproveAndExecuteAsync(
            principal.PrincipalId, proposal.Action!.ActionId, proposal.Action.Version,
            $"approval-{dependency}", now.AddSeconds(1)));
        Assert.Null(capability.LastInvocation);
    }

    [Fact]
    public async Task Unexpected_external_exception_transitions_started_action_to_reconciliation()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore(); await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var principal = PrincipalRef.Create("https://issuer.example", "tenant", "subject", "owner", now);
        await store.AddAsync(principal);
        await SeedGitHubDependencies(store,principal,now);
        var registry = new CapabilityRegistry(); registry.Register(new ThrowingCapability());
        var coordinator = new ExecutionCoordinator(registry,store,store,store,store,store);
        var proposal = await coordinator.ExecuteOrProposeAsync(Request(principal.PrincipalId,"target-a"),now);

        var result = await coordinator.ApproveAndExecuteAsync(
            principal.PrincipalId,proposal.Action!.ActionId,proposal.Action.Version,"approval-unknown",now.AddSeconds(1));

        Assert.Equal(ActionState.ReconciliationRequired,result.Action!.State);
        Assert.Equal("provider_unknown_exception",result.Action.Failure);
        Assert.Equal(CapabilityOutcome.UnknownOutcome,result.Result!.Outcome);
    }

    [Fact]
    public async Task Canceled_external_dispatch_transitions_started_action_to_reconciliation()
    {
        using var database=new TemporaryDatabase();var store=database.CreateStore();await store.InitializeAsync();var now=DateTimeOffset.UtcNow;var principal=PrincipalRef.Create("https://issuer.example","tenant","subject","owner",now);await store.AddAsync(principal);await SeedGitHubDependencies(store,principal,now);var capability=new CancelingCapability();var registry=new CapabilityRegistry();registry.Register(capability);var coordinator=new ExecutionCoordinator(registry,store,store,store,store,store);var proposal=await coordinator.ExecuteOrProposeAsync(Request(principal.PrincipalId,"target-a"),now);using var source=new CancellationTokenSource();var pending=coordinator.ApproveAndExecuteAsync(principal.PrincipalId,proposal.Action!.ActionId,proposal.Action.Version,"cancel-approval",now.AddSeconds(1),source.Token);await capability.Started;source.Cancel();var result=await pending;
        Assert.Equal(ActionState.ReconciliationRequired,result.Action!.State);Assert.Equal("provider_canceled_outcome_unknown",result.Action.Failure);
    }

    [Fact]
    public async Task Plugin_disable_after_final_check_before_dispatch_is_blocked_atomically()
    {
        using var database=new TemporaryDatabase();var store=database.CreateStore();await store.InitializeAsync();var now=DateTimeOffset.UtcNow;var principal=PrincipalRef.Create("https://issuer.example","tenant","subject","owner",now);await store.AddAsync(principal);await SeedGitHubDependencies(store,principal,now);var capability=new RecordingCapability();var registry=new CapabilityRegistry();registry.Register(capability);var availability=new InterleavingAvailability(async()=>Assert.True(await store.SetPluginEnabledAsync(principal.PrincipalId,"github","1.0.0",1,false)));var coordinator=new ExecutionCoordinator(registry,store,store,store,availability,store);var proposal=await coordinator.ExecuteOrProposeAsync(Request(principal.PrincipalId,"target-a"),now);var action=proposal.Action!;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>coordinator.ApproveAndExecuteAsync(
            principal.PrincipalId,action.ActionId,action.Version,"approval-interleaved",now.AddSeconds(1)));

        Assert.Null(capability.LastInvocation);Assert.Equal(ActionState.Authorized,(await store.GetActionAsync(principal.PrincipalId,action.ActionId))!.State);
    }

    [Fact]
    public async Task Read_plugin_disable_after_availability_before_dispatch_is_blocked_atomically()
    {
        using var database=new TemporaryDatabase();var store=database.CreateStore();await store.InitializeAsync();var now=DateTimeOffset.UtcNow;var principal=PrincipalRef.Create("https://issuer.example","tenant","subject","owner",now);await store.AddAsync(principal);await store.AddPluginInstallationAsync(new(principal.PrincipalId,"local","1.0.0","Local","Tessera","hash",LocalManifest,"{}",true,now,now,1));var capability=new RecordingReadCapability();var registry=new CapabilityRegistry();registry.Register(capability);var trace=new InterleavingTrace(store,async()=>Assert.True(await store.SetPluginEnabledAsync(principal.PrincipalId,"local","1.0.0",1,false)));using var input=JsonDocument.Parse("{\"timeZone\":\"UTC\"}");var request=new ExecutionRequest(principal.PrincipalId,"read-race","local.time","1","local","1.0.0",null,"UTC","target",input.RootElement.Clone(),"read-race-key");var coordinator=new ExecutionCoordinator(registry,store,store,store,store,store,trace);

        await Assert.ThrowsAsync<InvalidOperationException>(()=>coordinator.ExecuteOrProposeAsync(request,now));

        Assert.Equal(0,capability.InvocationCount);
    }

    [Fact]
    public async Task Write_permission_downgrade_after_final_check_before_dispatch_is_blocked_atomically()
    {
        using var database=new TemporaryDatabase();var store=database.CreateStore();await store.InitializeAsync();var now=DateTimeOffset.UtcNow;var principal=PrincipalRef.Create("https://issuer.example","tenant","subject","owner",now);await store.AddAsync(principal);await SeedGitHubDependencies(store,principal,now);var capability=new RecordingCapability();var registry=new CapabilityRegistry();registry.Register(capability);var availability=new InterleavingAvailability(async()=>{var account=await store.GetConnectedAccountAsync(principal.PrincipalId,"account-1");await store.SetConnectedAccountValidationAsync(principal.PrincipalId,"account-1",account!.Version,AccountLifecycle.Connected,AccountHealth.Healthy,"42","octo",[],[],[],now);});var coordinator=new ExecutionCoordinator(registry,store,store,store,availability,store);var proposal=await coordinator.ExecuteOrProposeAsync(Request(principal.PrincipalId,"target-a"),now);var action=Assert.IsType<ActionRecord>(proposal.Action);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(()=>coordinator.ApproveAndExecuteAsync(principal.PrincipalId,action.ActionId,action.Version,"permission-race",now.AddSeconds(1)));

        Assert.Null(capability.LastInvocation);Assert.Equal(ActionState.Authorized,(await store.GetActionAsync(principal.PrincipalId,action.ActionId))!.State);
    }

    [Fact]
    public async Task Read_permission_downgrade_after_availability_before_dispatch_is_blocked_atomically()
    {
        using var database=new TemporaryDatabase();var store=database.CreateStore();await store.InitializeAsync();var now=DateTimeOffset.UtcNow;var principal=PrincipalRef.Create("https://issuer.example","tenant","subject","owner",now);await store.AddAsync(principal);await store.AddPluginInstallationAsync(new(principal.PrincipalId,"github","1.0.0","GitHub","Tessera","hash",GitHubReadManifest,"{}",true,now,now,1));await store.AddConnectedAccountAsync(new(principal.PrincipalId,"account-1","github","github","1.0.0","GitHub",null,AccountLifecycle.Connected,ConnectedAccountCredentialRef.Create(principal.PrincipalId,"account-1"),AccountHealth.Healthy,null,"{}",["issues:read"],[new("github","1.0.0","github.issues.list","1")],now,now,1));await store.AddConversationAsync(new(principal.PrincipalId,"conversation-1","Test","ACTIVE",null,now,now,1));Assert.True(await store.ReplaceConversationGrantsAsync(principal.PrincipalId,"conversation-1",1,["account-1"],[("github.issues.list","1")]));var capability=new PermissionReadCapability();var registry=new CapabilityRegistry();registry.Register(capability);var trace=new InterleavingTrace(store,async()=>{var account=await store.GetConnectedAccountAsync(principal.PrincipalId,"account-1");await store.SetConnectedAccountValidationAsync(principal.PrincipalId,"account-1",account!.Version,AccountLifecycle.Connected,AccountHealth.Healthy,"42","octo",[],[],[],now);});using var input=JsonDocument.Parse("{\"repository\":\"owner/repo\"}");var request=new ExecutionRequest(principal.PrincipalId,"read-permission-race","github.issues.list","1","github","1.0.0","account-1","owner/repo","target",input.RootElement.Clone(),"read-permission-race",ConversationId:"conversation-1");var coordinator=new ExecutionCoordinator(registry,store,store,store,store,store,trace);

        await Assert.ThrowsAsync<InvalidOperationException>(()=>coordinator.ExecuteOrProposeAsync(request,now));

        Assert.Equal(0,capability.InvocationCount);
    }

    [Fact]
    public async Task Completed_read_trace_replays_without_duplicate_provider_invocation()
    {
        using var database=new TemporaryDatabase();var store=database.CreateStore();await store.InitializeAsync();var now=DateTimeOffset.UtcNow;var principal=PrincipalRef.Create("https://issuer.example","tenant","subject","owner",now);await store.AddAsync(principal);await store.AddPluginInstallationAsync(new(principal.PrincipalId,"local","1.0.0","Local","Tessera","hash",LocalManifest,"{}",true,now,now,1));var capability=new RecordingReadCapability();var registry=new CapabilityRegistry();registry.Register(capability);using var input=JsonDocument.Parse("{\"timeZone\":\"UTC\"}");var request=new ExecutionRequest(principal.PrincipalId,"read-replay","local.time","1","local","1.0.0",null,"UTC","target",input.RootElement.Clone(),"read-replay-key");var coordinator=new ExecutionCoordinator(registry,store,store,store,store,store,store);

        var first=await coordinator.ExecuteOrProposeAsync(request,now);var replay=await coordinator.ExecuteOrProposeAsync(request,now.AddSeconds(1));

        Assert.Equal(1,capability.InvocationCount);Assert.Equal(first.Result!.Output.GetRawText(),replay.Result!.Output.GetRawText());
    }

    [Fact]
    public async Task Unsafe_provider_result_fails_without_persisting_credential_value()
    {
        using var database=new TemporaryDatabase();var store=database.CreateStore();await store.InitializeAsync();var now=DateTimeOffset.UtcNow;var principal=PrincipalRef.Create("https://issuer.example","tenant","subject","owner",now);await store.AddAsync(principal);await store.AddPluginInstallationAsync(new(principal.PrincipalId,"local","1.0.0","Local","Tessera","hash",LocalManifest,"{}",true,now,now,1));var registry=new CapabilityRegistry();registry.Register(new UnsafeReadCapability());using var input=JsonDocument.Parse("{\"timeZone\":\"UTC\"}");var request=new ExecutionRequest(principal.PrincipalId,"unsafe-result","local.time","1","local","1.0.0",null,"UTC","target",input.RootElement.Clone(),"unsafe-result");var coordinator=new ExecutionCoordinator(registry,store,store,store,store,store,store);

        var response=await coordinator.ExecuteOrProposeAsync(request,now);var result=Assert.IsType<CapabilityResult>(response.Result);var persisted=Assert.Single(await store.ListCapabilityResultsAsync(principal.PrincipalId,null));

        Assert.Equal(CapabilityOutcome.Failed,result.Outcome);Assert.Equal("provider_unsafe_content",result.FailureCode);Assert.DoesNotContain("opaque-credential-value",persisted.DataJson,StringComparison.Ordinal);Assert.Equal("{}",persisted.DataJson);
    }

    private static CapabilityRegistry Registry()
    {
        var registry = new CapabilityRegistry();
        registry.Register(new DeterministicCapability(CapabilityDescriptor.Create(
            "github.issues.create", "1", "Create issue", "{}", "{}", SideEffectClass.ExternalCommunication,
            ["issues:write"], [], IdempotencySupport.ProviderNative, VerificationSupport.ProviderState),
            invocation => new(CapabilityOutcome.Succeeded, invocation.Input.Clone(), "issue:1", "verified", null)));
        return registry;
    }

    private static ExecutionCoordinator Coordinator(SqliteKernelStore store, CapabilityRegistry registry)
        => new(registry, store, store, store, new AlwaysAvailable(), store);

    private static async Task SeedGitHubDependencies(SqliteKernelStore store,PrincipalRef principal,DateTimeOffset now)
    {
        await store.AddPluginInstallationAsync(new(principal.PrincipalId,"github","1.0.0","GitHub","Tessera","hash",GitHubManifest,"{}",true,now,now,1));
        await store.AddConnectedAccountAsync(new(principal.PrincipalId,"account-1","github","github","1.0.0","GitHub",null,AccountLifecycle.Connected,
            ConnectedAccountCredentialRef.Create(principal.PrincipalId,"account-1"),AccountHealth.Healthy,null,"{}",["issues:write"],
            [new("github","1.0.0","github.issues.create","1")],now,now,1));
        await store.AddConversationAsync(new(principal.PrincipalId,"conversation-1","Test","ACTIVE",null,now,now,1));
        Assert.True(await store.ReplaceConversationGrantsAsync(principal.PrincipalId,"conversation-1",1,["account-1"],[("github.issues.create","1")]));
    }

    private static ExecutionRequest Request(string owner, string targetHash)
    {
        using var document = JsonDocument.Parse("{\"title\":\"Safe issue\"}");
        return new(owner,"execution-1","github.issues.create","1","github","1.0.0","account-1",
            "owner/repo",targetHash,document.RootElement.Clone(),"idem-1",ConversationId:"conversation-1");
    }

    private sealed class AlwaysAvailable : ICapabilityAvailability
    {
        public ValueTask<ExecutionDecision> CheckAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new ExecutionDecision(true));
    }

    private sealed class InterleavingAvailability(Func<Task> mutate) : ICapabilityAvailability
    {
        private int _checks;
        public async ValueTask<ExecutionDecision> CheckAsync(ExecutionRequest request,CancellationToken cancellationToken=default)
        {if(Interlocked.Increment(ref _checks)==3)await mutate();return new(true);}
    }

    private sealed class RecordingCapability : ICapability
    {
        public CapabilityDescriptor Descriptor { get; } = CapabilityDescriptor.Create(
            "github.issues.create", "1", "Create issue", "{}", "{}", SideEffectClass.ExternalCommunication,
            ["issues:write"], [], IdempotencySupport.ProviderNative, VerificationSupport.ProviderState);

        public CapabilityInvocation? LastInvocation { get; private set; }

        public ValueTask<CapabilityResult> InvokeAsync(
            CapabilityInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            LastInvocation = invocation;
            return ValueTask.FromResult(new CapabilityResult(
                CapabilityOutcome.Succeeded, invocation.Input.Clone(), "issue:1", "verified", null));
        }
    }

    private sealed class ThrowingCapability : ICapability
    {
        public CapabilityDescriptor Descriptor { get; } = CapabilityDescriptor.Create(
            "github.issues.create","1","Create issue","{}","{}",SideEffectClass.ExternalCommunication,
            ["issues:write"],[],IdempotencySupport.ProviderNative,VerificationSupport.ProviderState);
        public ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation,CancellationToken cancellationToken=default)
            => throw new HttpRequestException("upstream disconnected after dispatch");
    }

    private sealed class CancelingCapability:ICapability
    {private readonly TaskCompletionSource _started=new(TaskCreationOptions.RunContinuationsAsynchronously);public Task Started=>_started.Task;public CapabilityDescriptor Descriptor{get;}=CapabilityDescriptor.Create("github.issues.create","1","Create issue","{}","{}",SideEffectClass.ExternalCommunication,["issues:write"],[],IdempotencySupport.ProviderNative,VerificationSupport.ProviderState);public async ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation,CancellationToken cancellationToken=default){_started.TrySetResult();await Task.Delay(Timeout.InfiniteTimeSpan,cancellationToken);throw new InvalidOperationException();}}

    private sealed class RecordingReadCapability:ICapability
    {public int InvocationCount{get;private set;}public CapabilityDescriptor Descriptor{get;}=CapabilityDescriptor.Create("local.time","1","Read","{}","{}",SideEffectClass.ReadOnly,[],[SensitivityClass.Internal],IdempotencySupport.Keyed,VerificationSupport.None);public ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation,CancellationToken cancellationToken=default){InvocationCount++;return ValueTask.FromResult(new CapabilityResult(CapabilityOutcome.Succeeded,invocation.Input.Clone(),null,null,null));}}

    private sealed class PermissionReadCapability:ICapability
    {public int InvocationCount{get;private set;}public CapabilityDescriptor Descriptor{get;}=CapabilityDescriptor.Create("github.issues.list","1","Read issues","{}","{}",SideEffectClass.ReadOnly,["issues:read"],[SensitivityClass.Internal],IdempotencySupport.Keyed,VerificationSupport.None);public ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation,CancellationToken cancellationToken=default){InvocationCount++;return ValueTask.FromResult(new CapabilityResult(CapabilityOutcome.Succeeded,invocation.Input.Clone(),null,null,null));}}

    private sealed class UnsafeReadCapability:ICapability
    {public CapabilityDescriptor Descriptor{get;}=CapabilityDescriptor.Create("local.time","1","Unsafe read","{}","{}",SideEffectClass.ReadOnly,[],[SensitivityClass.Internal],IdempotencySupport.Keyed,VerificationSupport.None);public ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation,CancellationToken cancellationToken=default)=>ValueTask.FromResult(new CapabilityResult(CapabilityOutcome.Succeeded,JsonSerializer.SerializeToElement(new{token="opaque-credential-value"}),null,null,null));}

    private sealed class InterleavingTrace(SqliteKernelStore store,Func<Task> mutate):ICapabilityTraceRepository
    {public Task BeginCapabilityCallAsync(ExecutionRequest request,DateTimeOffset now,CancellationToken token=default)=>store.BeginCapabilityCallAsync(request,now,token);public Task<CapabilityResult?> GetCompletedCapabilityResultAsync(ExecutionRequest request,CancellationToken token=default)=>store.GetCompletedCapabilityResultAsync(request,token);public async Task<bool> TryStartCapabilityCallAsync(ExecutionRequest request,DateTimeOffset now,CancellationToken token=default){await mutate();return await store.TryStartCapabilityCallAsync(request,now,token);}public Task CompleteCapabilityCallAsync(ExecutionRequest request,CapabilityResult result,DateTimeOffset now,CancellationToken token=default)=>store.CompleteCapabilityCallAsync(request,result,now,token);public Task<IReadOnlyList<ProductCapabilityCall>> ListCapabilityCallsAsync(string owner,string? jobRunId,CancellationToken token=default)=>store.ListCapabilityCallsAsync(owner,jobRunId,token);public Task<IReadOnlyList<ProductCapabilityResult>> ListCapabilityResultsAsync(string owner,string? jobRunId,CancellationToken token=default)=>store.ListCapabilityResultsAsync(owner,jobRunId,token);}

    private const string GitHubManifest = """{"Id":"github","Version":"1.0.0","Name":"GitHub","Publisher":"Tessera","MinimumTesseraVersion":"2.0.0","Capabilities":[{"Id":"github.issues.create","Version":"1","Description":"Create issue","ExecutorKind":"github-rest","AccountRequired":true,"RequiredPermissions":["issues:write"],"SideEffectClass":"ExternalCommunication","TimeoutMilliseconds":30000,"MaxResultBytes":32768}]}""";
    private const string GitHubReadManifest = """{"Id":"github","Version":"1.0.0","Name":"GitHub","Publisher":"Tessera","MinimumTesseraVersion":"2.0.0","Capabilities":[{"Id":"github.issues.list","Version":"1","Description":"List issues","ExecutorKind":"github-rest","AccountRequired":true,"RequiredPermissions":["issues:read"],"SideEffectClass":"ReadOnly","TimeoutMilliseconds":30000,"MaxResultBytes":32768}]}""";
    private const string LocalManifest = """{"Id":"local","Version":"1.0.0","Name":"Local","Publisher":"Tessera","MinimumTesseraVersion":"2.0.0","Capabilities":[{"Id":"local.time","Version":"1","Description":"Time","ExecutorKind":"local-date-time","AccountRequired":false,"RequiredPermissions":[],"SideEffectClass":"ReadOnly","TimeoutMilliseconds":1000,"MaxResultBytes":4096}]}""";
}