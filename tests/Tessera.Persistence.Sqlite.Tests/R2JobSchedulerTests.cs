using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Xunit;

namespace Tessera.Persistence.Sqlite.Tests;

public sealed class R2JobSchedulerTests
{
    [Theory]
    [InlineData(CapabilityOutcome.Succeeded, "SUCCEEDED")]
    [InlineData(CapabilityOutcome.UnknownOutcome, "RECONCILIATION_REQUIRED")]
    public async Task Side_effect_run_checkpoints_approval_and_resumes_exactly_once_after_restart(
        CapabilityOutcome outcome,
        string expectedRunState)
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var owner = PrincipalRef.Create("https://issuer.example", "tenant", "subject", "owner", now);
        await store.AddAsync(owner);
        await AddGitHubDependencyAsync(store, owner.PrincipalId, now);
        var schedule = new JobSchedule("once", now, null, "UTC", null);
        await store.AddJobAsync(new(owner.PrincipalId, "j1", "Create issue", "Create the approved issue",
            "ACTIVE", "READY", null, schedule, null, "{}", ["account-1"],
            [("github.issues.create", "1")], ["ExternalCommunication"], now, now, 1));
        var run = await store.CreateRunOccurrenceAsync(owner.PrincipalId, "j1", now);
        Assert.NotNull(run);
        var fence = await store.AcquireRunLeaseAsync(
            owner.PrincipalId, run.RunId, "worker-1", now, TimeSpan.FromMinutes(1));
        Assert.NotNull(fence);
        Assert.True(await store.StartRunAsync(owner.PrincipalId, run.RunId, run.Version, fence.Value, now));

        var capability = new CountingCapability(outcome);
        var registry = new CapabilityRegistry();
        registry.Register(capability);
        using var input = System.Text.Json.JsonDocument.Parse("{\"title\":\"Approved issue\"}");
        var request = new ExecutionRequest(owner.PrincipalId, run.RunId, "github.issues.create", "1",
            "github", "1.0.0", "account-1", "owner/repo", "target-hash", input.RootElement.Clone(),
            "run-side-effect-1", JobId: "j1", JobRunId: run.RunId);
        var coordinator = new ExecutionCoordinator(registry, store, store, store, store, store);
        var proposal = await coordinator.ExecuteOrProposeAsync(request, now.AddSeconds(1));
        Assert.True(await store.WaitForRunApprovalAsync(
            owner.PrincipalId, run.RunId, fence.Value, proposal.Action!.ActionId, request, now.AddSeconds(1)));

        var restarted = database.CreateStore();
        await restarted.InitializeAsync();
        var afterRestart = new ExecutionCoordinator(registry, restarted, restarted, restarted, restarted, restarted);
        var actionOutcome = await afterRestart.ApproveAndExecuteAsync(owner.PrincipalId, proposal.Action.ActionId,
            proposal.Action.Version, "approve-run-1", now.AddSeconds(2));
        var resumeFence = await restarted.AcquireRunLeaseAsync(
            owner.PrincipalId, run.RunId, "worker-2", now.AddSeconds(3), TimeSpan.FromMinutes(1));
        Assert.NotNull(resumeFence);
        var resolved = await restarted.ResolveWaitingRunAsync(
            owner.PrincipalId, run.RunId, resumeFence.Value, now.AddSeconds(3));
        Assert.NotNull(resolved);

        Assert.Equal(expectedRunState, resolved.State);
        Assert.Equal(1, capability.InvocationCount);
        Assert.Null(await restarted.ResolveWaitingRunAsync(
            owner.PrincipalId, run.RunId, resumeFence.Value, now.AddSeconds(4)));
        Assert.Equal(1, capability.InvocationCount);
        if (outcome == CapabilityOutcome.UnknownOutcome)
        {
            var canceled = await restarted.CancelActionAsync(owner.PrincipalId, proposal.Action.ActionId,
                actionOutcome.Action!.Version, now.AddSeconds(5));
            Assert.NotNull(canceled);
            var resolvedCanceled = await restarted.ResolveWaitingRunAsync(
                owner.PrincipalId, run.RunId, resumeFence.Value, now.AddSeconds(5));
            Assert.Equal("CANCELED", resolvedCanceled!.State);
            Assert.Equal(1, capability.InvocationCount);
        }
    }

    [Fact]
    public async Task Occurrence_is_unique_and_lease_fence_rejects_stale_worker_after_restart()
    {
        using var database=new TemporaryDatabase();var store=database.CreateStore();await store.InitializeAsync();var now=DateTimeOffset.UtcNow;
        var owner=PrincipalRef.Create("https://issuer.example","tenant","subject","owner",now);await store.AddAsync(owner);
        var schedule=new JobSchedule("once",now.AddMinutes(1),null,"UTC",null);await store.AddJobAsync(new(owner.PrincipalId,"j1","Job","Do work","ACTIVE","READY",null,schedule,schedule.At,"{}",[],[],[],now,now,1));
        var run=await store.CreateRunOccurrenceAsync(owner.PrincipalId,"j1",schedule.At!.Value);Assert.NotNull(run);Assert.Null(await store.CreateRunOccurrenceAsync(owner.PrincipalId,"j1",schedule.At.Value));
        var fence1=await store.AcquireRunLeaseAsync(owner.PrincipalId,run!.RunId,"worker-1",now,TimeSpan.FromSeconds(1));Assert.Equal(1,fence1);
        var restarted=database.CreateStore();await restarted.InitializeAsync();var fence2=await restarted.AcquireRunLeaseAsync(owner.PrincipalId,run.RunId,"worker-2",now.AddSeconds(2),TimeSpan.FromMinutes(1));Assert.Equal(2,fence2);
        Assert.False(await restarted.AddRunCheckpointAsync(owner.PrincipalId,run.RunId,1,"stale","{}",fence1!.Value,now.AddSeconds(2)));
        Assert.True(await restarted.AddRunCheckpointAsync(owner.PrincipalId,run.RunId,1,"current","{}",fence2!.Value,now.AddSeconds(2)));
    }

    [Fact]
    public async Task Expired_running_read_run_is_requeued_without_duplicate_occurrence()
    {
        using var database=new TemporaryDatabase();var store=database.CreateStore();await store.InitializeAsync();var now=DateTimeOffset.UtcNow;
        var owner=PrincipalRef.Create("https://issuer.example","tenant","subject","owner",now);await store.AddAsync(owner);
        var schedule=new JobSchedule("once",now,null,"UTC",null);await store.AddJobAsync(new(owner.PrincipalId,"recover-job","Recover","Read-only work","ACTIVE","READY",null,schedule,null,"{}",[],[],[],now,now,1));
        var run=await store.CreateRunOccurrenceAsync(owner.PrincipalId,"recover-job",now);Assert.NotNull(run);
        var fence=await store.AcquireRunLeaseAsync(owner.PrincipalId,run!.RunId,"dead-worker",now,TimeSpan.FromSeconds(1));Assert.NotNull(fence);
        Assert.True(await store.StartRunAsync(owner.PrincipalId,run.RunId,run.Version,fence.Value,now));

        var restarted=database.CreateStore();await restarted.InitializeAsync();
        Assert.Equal(1,await restarted.RecoverExpiredRunningRunsAsync(now.AddSeconds(2)));
        Assert.Equal("QUEUED",(await restarted.GetJobRunAsync(owner.PrincipalId,run.RunId))!.State);
        Assert.Null(await restarted.CreateRunOccurrenceAsync(owner.PrincipalId,"recover-job",now));
    }

    [Fact]
    public async Task Expired_run_with_durable_proposal_recovers_to_waiting_without_dispatch()
    {
        using var database=new TemporaryDatabase();var store=database.CreateStore();await store.InitializeAsync();var now=DateTimeOffset.UtcNow;var owner=PrincipalRef.Create("https://issuer.example","tenant","subject","owner",now);await store.AddAsync(owner);await AddGitHubDependencyAsync(store,owner.PrincipalId,now);var schedule=new JobSchedule("once",now,null,"UTC",null);await store.AddJobAsync(new(owner.PrincipalId,"proposal-job","Proposal","Create issue","ACTIVE","READY",null,schedule,null,"{}",["account-1"],[("github.issues.create","1")],["ExternalCommunication"],now,now,1));var run=await store.CreateRunOccurrenceAsync(owner.PrincipalId,"proposal-job",now);Assert.NotNull(run);var fence=await store.AcquireRunLeaseAsync(owner.PrincipalId,run!.RunId,"dead-worker",now,TimeSpan.FromSeconds(1));Assert.NotNull(fence);Assert.True(await store.StartRunAsync(owner.PrincipalId,run.RunId,run.Version,fence.Value,now));var capability=new CountingCapability(CapabilityOutcome.Succeeded);var registry=new CapabilityRegistry();registry.Register(capability);using var input=System.Text.Json.JsonDocument.Parse("{\"title\":\"Review me\"}");var request=new ExecutionRequest(owner.PrincipalId,run.RunId,"github.issues.create","1","github","1.0.0","account-1","owner/repo","target",input.RootElement.Clone(),"proposal-key",JobId:"proposal-job",JobRunId:run.RunId);var proposal=await new ExecutionCoordinator(registry,store,store,store,store,store).ExecuteOrProposeAsync(request,now);Assert.NotNull(proposal.Action);

        var restarted=database.CreateStore();await restarted.InitializeAsync();Assert.Equal(1,await restarted.RecoverExpiredRunningRunsAsync(now.AddSeconds(2)));

        Assert.Equal("WAITING_FOR_APPROVAL",(await restarted.GetJobRunAsync(owner.PrincipalId,run.RunId))!.State);Assert.Equal(0,capability.InvocationCount);
    }

    [Fact]
    public async Task Queued_run_cannot_start_after_job_is_paused()
    {
        using var database=new TemporaryDatabase();var store=database.CreateStore();await store.InitializeAsync();var now=DateTimeOffset.UtcNow;var owner=PrincipalRef.Create("https://issuer.example","tenant","subject","owner",now);await store.AddAsync(owner);var schedule=new JobSchedule("once",now,null,"UTC",null);await store.AddJobAsync(new(owner.PrincipalId,"paused-job","Paused","No dispatch","ACTIVE","READY",null,schedule,null,"{}",[],[],[],now,now,1));var run=await store.CreateRunOccurrenceAsync(owner.PrincipalId,"paused-job",now);Assert.NotNull(run);Assert.True(await store.SetJobDesiredStateAsync(owner.PrincipalId,"paused-job",1,"PAUSED"));var fence=await store.AcquireRunLeaseAsync(owner.PrincipalId,run!.RunId,"worker",now,TimeSpan.FromMinutes(2));Assert.NotNull(fence);

        Assert.False(await store.StartRunAsync(owner.PrincipalId,run.RunId,run.Version,fence.Value,now));
        Assert.Equal("QUEUED",(await store.GetJobRunAsync(owner.PrincipalId,run.RunId))!.State);
    }

    [Fact]
    public async Task Canceling_job_materializes_queued_runs_as_canceled()
    {using var database=new TemporaryDatabase();var store=database.CreateStore();await store.InitializeAsync();var now=DateTimeOffset.UtcNow;var owner=PrincipalRef.Create("https://issuer.example","tenant","subject","owner",now);await store.AddAsync(owner);var schedule=new JobSchedule("once",now,null,"UTC",null);await store.AddJobAsync(new(owner.PrincipalId,"cancel-job","Cancel","No work","ACTIVE","READY",null,schedule,null,"{}",[],[],[],now,now,1));var run=await store.CreateRunOccurrenceAsync(owner.PrincipalId,"cancel-job",now);Assert.NotNull(run);Assert.True(await store.SetJobDesiredStateAsync(owner.PrincipalId,"cancel-job",1,"CANCELED"));Assert.Equal("CANCELED",(await store.GetJobRunAsync(owner.PrincipalId,run!.RunId))!.State);}

    [Fact]
    public void Weekday_schedule_skips_weekend_in_iana_zone()
    {
        var friday=new DateTimeOffset(2026,8,7,13,0,0,TimeSpan.Zero);var schedule=new JobSchedule("weekday",null,new TimeOnly(8,0),"America/New_York",null);
        Assert.Equal(DayOfWeek.Monday,JobScheduleCalculator.Next(schedule,friday)!.Value.DayOfWeek);
    }

    [Fact]
    public async Task Scheduler_atomically_creates_due_run_and_advances_recurring_job()
    {
        using var database=new TemporaryDatabase();var store=database.CreateStore();await store.InitializeAsync();var now=DateTimeOffset.UtcNow;
        var owner=PrincipalRef.Create("https://issuer.example","tenant","subject","owner",now);await store.AddAsync(owner);
        var schedule=new JobSchedule("weekday",null,new TimeOnly(8,0),"UTC",null);
        await store.AddJobAsync(new(owner.PrincipalId,"j1","Daily","Do work","ACTIVE","READY",null,schedule,now,"{}",[],[],[],now,now,1));
        Assert.Equal(1,await store.ScheduleDueRunsAsync(now));
        Assert.Equal(0,await store.ScheduleDueRunsAsync(now));
        var job=Assert.Single(await store.ListJobsAsync(owner.PrincipalId));
        Assert.True(job.NextOccurrence>now);
        Assert.Equal(2,job.Version);
    }

    [Fact]
    public async Task Job_dispatch_is_denied_without_explicit_account_and_capability_grants()
    {
        using var database=new TemporaryDatabase();var store=database.CreateStore();await store.InitializeAsync();var now=DateTimeOffset.UtcNow;
        var owner=PrincipalRef.Create("https://issuer.example","tenant","subject","owner",now);await store.AddAsync(owner);
        await store.AddPluginInstallationAsync(new(owner.PrincipalId,"model-provider","1.0.0","Models","Tessera","hash",ModelManifest,"{}",true,now,now,1));
        await store.AddConnectedAccountAsync(new(owner.PrincipalId,"account-1","openai-compatible","model-provider","1.0.0","Model",null,AccountLifecycle.Connected,ConnectedAccountCredentialRef.Create(owner.PrincipalId,"account-1"),AccountHealth.Healthy,null,"{}",[],[new("model-provider","1.0.0","model.chat.complete","1")],now,now,1));
        await store.AddModelProfileAsync(new(owner.PrincipalId,"profile-1","account-1","openai-compatible-remote","https://models.example/v1","model",8192,true,true,true,now,now,1));
        var schedule=new JobSchedule("once",now.AddMinutes(1),null,"UTC",null);
        await store.AddJobAsync(new(owner.PrincipalId,"j1","Denied","Do work","ACTIVE","READY",null,schedule,schedule.At,"{}",[],[],[],now,now,1));
        using var input=System.Text.Json.JsonDocument.Parse("{}");
        var request=new ExecutionRequest(owner.PrincipalId,"r1","model.chat.complete","1","model-provider","1.0.0","account-1","model","hash",input.RootElement.Clone(),"key",JobId:"j1",JobRunId:"r1");
        Assert.Equal("job_account_not_granted",(await store.CheckAsync(request)).BlockedCode);
    }

    private static async Task AddGitHubDependencyAsync(SqliteKernelStore store, string owner, DateTimeOffset now)
    {
        await store.AddPluginInstallationAsync(new(owner, "github", "1.0.0", "GitHub", "Tessera",
            "sha256:package", GitHubManifest, "{}", true, now, now, 1));
        await store.AddConnectedAccountAsync(new(owner, "account-1", "github", "github", "1.0.0",
            "GitHub", null, AccountLifecycle.Connected, ConnectedAccountCredentialRef.Create(owner,"account-1"), AccountHealth.Healthy,
            null, "{}", ["issues:write"], [new("github", "1.0.0", "github.issues.create", "1")], now, now, 1));
    }

    private const string GitHubManifest = """{"Id":"github","Version":"1.0.0","Name":"GitHub","Publisher":"Tessera","MinimumTesseraVersion":"2.0.0","Capabilities":[{"Id":"github.issues.create","Version":"1","Description":"Create issue","ExecutorKind":"github-rest","AccountRequired":true,"RequiredPermissions":["issues:write"],"SideEffectClass":"ExternalCommunication","TimeoutMilliseconds":30000,"MaxResultBytes":32768}]}""";
    private const string ModelManifest = """{"Id":"model-provider","Version":"1.0.0","Name":"Models","Publisher":"Tessera","MinimumTesseraVersion":"2.0.0","Capabilities":[{"Id":"model.chat.complete","Version":"1","Description":"Complete","ExecutorKind":"openai-compatible","AccountRequired":true,"RequiredPermissions":[],"SideEffectClass":"ReadOnly","TimeoutMilliseconds":120000,"MaxResultBytes":1048576}]}""";

    private sealed class CountingCapability(CapabilityOutcome outcome) : ICapability
    {
        public CapabilityDescriptor Descriptor { get; } = CapabilityDescriptor.Create(
            "github.issues.create", "1", "Create issue", "{}", "{}", SideEffectClass.ExternalCommunication,
            ["issues:write"], [], IdempotencySupport.ProviderNative, VerificationSupport.ProviderState);

        public int InvocationCount { get; private set; }

        public ValueTask<CapabilityResult> InvokeAsync(
            CapabilityInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            return ValueTask.FromResult(new CapabilityResult(
                outcome, invocation.Input.Clone(), outcome == CapabilityOutcome.Succeeded ? "issue:1" : null,
                outcome == CapabilityOutcome.Succeeded ? "verified" : null,
                outcome == CapabilityOutcome.UnknownOutcome ? "provider_outcome_unknown" : null));
        }
    }
}