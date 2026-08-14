using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
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
    public async Task Expired_running_run_expires_authorized_not_started_action_and_requeues_safely()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var owner = PrincipalRef.Create("https://issuer.example", "tenant", "authorized-recovery", "owner", now);
        await store.AddAsync(owner);
        await store.AddJobAsync(new(owner.PrincipalId, "authorized-job", "Authorized", "Read work", "ACTIVE", "READY", null,
            new JobSchedule("once", now, null, "UTC", null), null, "{}", [], [], [], now, now, 1));
        var run = await store.CreateRunOccurrenceAsync(owner.PrincipalId, "authorized-job", now);
        var fence = await store.AcquireRunLeaseAsync(owner.PrincipalId, run!.RunId, "dead-worker", now, TimeSpan.FromSeconds(1));
        Assert.True(await store.StartRunAsync(owner.PrincipalId, run.RunId, run.Version, fence!.Value, now));
        var action = KernelTestData.Action(owner.PrincipalId, "authorized-action").BindR2(new ActionR2Binding(
            null, "local", "1.0.0", new string('a', 64), now.AddMinutes(5), "authorized-execution",
            jobId: "authorized-job", jobRunId: run.RunId));
        await store.AddAsync(owner.PrincipalId, action);
        await SetActionStateAsync(database.Path, owner.PrincipalId, action.ActionId, "AUTHORIZED", "authorization-1", now);

        Assert.Equal(1, await store.RecoverExpiredRunningRunsAsync(now.AddSeconds(2)));
        Assert.Equal("QUEUED", (await store.GetJobRunAsync(owner.PrincipalId, run.RunId))!.State);
        var recovered = await store.GetActionAsync(owner.PrincipalId, action.ActionId);
        Assert.Equal(ActionState.Expired, recovered!.State);
        Assert.Equal("authorized_action_recovered_not_started", recovered.Failure);
    }

    [Fact]
    public async Task Expired_running_run_projects_latest_terminal_success_action_without_replay()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var owner = PrincipalRef.Create("https://issuer.example", "tenant", "terminal-recovery", "owner", now);
        await store.AddAsync(owner);
        await store.AddJobAsync(new(owner.PrincipalId, "terminal-job", "Terminal", "Read work", "ACTIVE", "READY", null,
            new JobSchedule("once", now, null, "UTC", null), null, "{}", [], [], [], now, now, 1));
        var run = await store.CreateRunOccurrenceAsync(owner.PrincipalId, "terminal-job", now);
        var fence = await store.AcquireRunLeaseAsync(owner.PrincipalId, run!.RunId, "dead-worker", now, TimeSpan.FromSeconds(1));
        Assert.True(await store.StartRunAsync(owner.PrincipalId, run.RunId, run.Version, fence!.Value, now));
        var action = KernelTestData.Action(owner.PrincipalId, "terminal-action").BindR2(new ActionR2Binding(
            null, "local", "1.0.0", new string('a', 64), now.AddMinutes(5), "terminal-execution",
            jobId: "terminal-job", jobRunId: run.RunId));
        await store.AddAsync(owner.PrincipalId, action);
        await SetActionStateAsync(database.Path, owner.PrincipalId, action.ActionId, "EXTERNALLY_CONFIRMED", "authorization-2", now);

        Assert.Equal(1, await store.RecoverExpiredRunningRunsAsync(now.AddSeconds(2)));
        var recovered = await store.GetJobRunAsync(owner.PrincipalId, run.RunId);
        Assert.Equal("SUCCEEDED", recovered!.State);
        Assert.NotNull(recovered.EndedAt);
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

    [Fact]
    public async Task Compatible_host_job_with_zero_hosts_stays_queued_and_materializes_one_active_blocker()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var owner = PrincipalRef.Create("https://issuer.example", "tenant", "subject", "owner", now);
        await store.AddAsync(owner);
        var schedule = new JobSchedule("once", now, null, "UTC", null);
        await store.AddJobAsync(new(owner.PrincipalId, "host-job", "Host job", "Inspect repository", "ACTIVE", "READY", null,
            schedule, null, "{}", [], [("host.repo.identity", "1")], [], now, now, 1));
        await store.PutJobExecutionPolicyAsync(new(
            owner.PrincipalId,
            "host-job",
            JobExecutionLocations.AnyCompatibleHost,
            null,
            [(RemoteHostValidation.SupportedCapabilityId, RemoteHostValidation.SupportedCapabilityVersion)],
            ["repo-main"],
            JobExecutionFallbackPolicies.None,
            1), 0);
        var run = await store.CreateRunOccurrenceAsync(owner.PrincipalId, "host-job", now);
        Assert.NotNull(run);

        var fence = await store.AcquireRunLeaseAsync(owner.PrincipalId, run!.RunId, "worker", now, TimeSpan.FromMinutes(10));
        Assert.NotNull(fence);
        var dispatch = await store.PrepareHostDispatchAsync(
            (await store.GetJobAsync(owner.PrincipalId, "host-job"))!,
            run,
            fence!.Value,
            now,
            TimeSpan.FromMinutes(10));
        Assert.True(dispatch.RoutedToHost);
        Assert.NotNull(dispatch.Blocker);

        var refreshed = await store.GetJobRunAsync(owner.PrincipalId, run.RunId);
        Assert.Equal("QUEUED", refreshed!.State);
        var projection = await store.GetRemoteJobRunProjectionAsync(owner.PrincipalId, run.RunId);
        Assert.NotNull(projection);
        Assert.Equal(JobRunBlockerCodes.WaitingForHost, projection!.Blocker?.Code);
        Assert.Null(projection.Lease);
    }

    [Fact]
    public async Task Cleared_host_blocker_recreates_as_the_next_append_only_version()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var owner = PrincipalRef.Create("https://issuer.example", "tenant", "blocker-owner", "owner", now);
        await store.AddAsync(owner);
        var job = new ProductJob(owner.PrincipalId, "blocker-job", "Host job", "Inspect repository",
            "ACTIVE", "READY", null, new JobSchedule("once", now, null, "UTC", null), null,
            "{}", [], [("host.repo.identity", "1")], [], now, now, 1);
        await store.AddJobAsync(job);
        await store.PutJobExecutionPolicyAsync(new(
            owner.PrincipalId, job.JobId, JobExecutionLocations.AnyCompatibleHost, null,
            [(RemoteHostValidation.SupportedCapabilityId, RemoteHostValidation.SupportedCapabilityVersion)],
            ["repo-main"], JobExecutionFallbackPolicies.None, 1), 0);
        var run = await store.CreateRunOccurrenceAsync(owner.PrincipalId, job.JobId, now);
        Assert.NotNull(run);

        var firstFence = await store.AcquireRunLeaseAsync(
            owner.PrincipalId, run!.RunId, "worker-1", now, TimeSpan.FromMinutes(1));
        Assert.NotNull(firstFence);
        Assert.NotNull((await store.PrepareHostDispatchAsync(
            job, run, firstFence!.Value, now, TimeSpan.FromMinutes(1))).Blocker);
        await ClearBlockerAsync(database.Path, owner.PrincipalId, run.RunId, now.AddSeconds(1));

        var secondFence = await store.AcquireRunLeaseAsync(
            owner.PrincipalId, run.RunId, "worker-2", now.AddSeconds(2), TimeSpan.FromMinutes(1));
        Assert.NotNull(secondFence);
        var second = await store.PrepareHostDispatchAsync(
            job, run, secondFence!.Value, now.AddSeconds(2), TimeSpan.FromMinutes(1));
        Assert.Equal(2, second.Blocker!.Version);
        Assert.Equal([1L, 2L], await ReadBlockerVersionsAsync(database.Path, owner.PrincipalId, run.RunId));
    }

    [Fact]
    public async Task Explicit_host_job_offers_a_lease_to_a_compatible_online_host_without_starting_the_run()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var owner = PrincipalRef.Create("https://issuer.example", "tenant", "subject", "owner", now);
        await store.AddAsync(owner);
        await SeedOnlineHostAsync(database.Path, owner.PrincipalId, "host-online", "repo-main");
        var schedule = new JobSchedule("once", now, null, "UTC", null);
        await store.AddJobAsync(new(owner.PrincipalId, "host-job", "Host job", "Inspect repository", "ACTIVE", "READY", null,
            schedule, null, "{}", [], [("host.repo.identity", "1")], [], now, now, 1));
        await store.PutJobExecutionPolicyAsync(new(
            owner.PrincipalId,
            "host-job",
            JobExecutionLocations.Host,
            "host-online",
            [(RemoteHostValidation.SupportedCapabilityId, RemoteHostValidation.SupportedCapabilityVersion)],
            ["repo-main"],
            JobExecutionFallbackPolicies.None,
            1), 0);
        var run = await store.CreateRunOccurrenceAsync(owner.PrincipalId, "host-job", now);
        Assert.NotNull(run);

        var fence = await store.AcquireRunLeaseAsync(owner.PrincipalId, run!.RunId, "worker", now, TimeSpan.FromMinutes(10));
        Assert.NotNull(fence);
        var dispatch = await store.PrepareHostDispatchAsync(
            (await store.GetJobAsync(owner.PrincipalId, "host-job"))!,
            run,
            fence!.Value,
            now,
            TimeSpan.FromMinutes(10));
        Assert.True(dispatch.RoutedToHost);

        var refreshed = await store.GetJobRunAsync(owner.PrincipalId, run.RunId);
        Assert.Equal("QUEUED", refreshed!.State);
        var projection = await store.GetRemoteJobRunProjectionAsync(owner.PrincipalId, run.RunId);
        Assert.NotNull(projection?.Lease);
        Assert.Equal(HostLeaseStates.Offered, projection!.Lease!.State);
        Assert.Equal("host-online", projection.Host?.HostId);
        Assert.Null(projection.Blocker);
        var hostDetail = await store.GetRemoteHostDetailAsync(owner.PrincipalId, "host-online");
        Assert.Equal(RemoteHostLifecycles.Busy, hostDetail!.Host.Lifecycle);
    }

    [Fact]
    public async Task Explicit_host_execution_policy_requires_a_preferred_host_id()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var owner = PrincipalRef.Create("https://issuer.example", "tenant", "subject", "owner", now);
        await store.AddAsync(owner);

        await Assert.ThrowsAsync<ArgumentException>(() => store.PutJobExecutionPolicyAsync(new(
            owner.PrincipalId,
            "job-host-invalid",
            JobExecutionLocations.Host,
            null,
            [(RemoteHostValidation.SupportedCapabilityId, RemoteHostValidation.SupportedCapabilityVersion)],
            ["repo-main"],
            JobExecutionFallbackPolicies.None,
            1), 0));
    }

    [Fact]
    public async Task Exact_preferred_offline_host_blocks_without_fallback_and_any_compatible_uses_the_online_host()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var owner = PrincipalRef.Create("https://issuer.example", "tenant", "subject", "owner", now);
        await store.AddAsync(owner);
        await SeedOnlineHostAsync(database.Path, owner.PrincipalId, "host-online", "repo-main");
        await SeedOnlineHostAsync(database.Path, owner.PrincipalId, "host-offline", "repo-main");
        await SetHostLifecycleAsync(database.Path, owner.PrincipalId, "host-offline", RemoteHostLifecycles.Offline);

        var exactJob = new ProductJob(owner.PrincipalId, "job-host-exact", "Host job", "Inspect repository", "ACTIVE", "READY", null,
            new JobSchedule("once", now, null, "UTC", null), null, "{}", [], [("host.repo.identity", "1")], [], now, now, 1);
        await store.AddJobAsync(exactJob);
        await store.PutJobExecutionPolicyAsync(new(
            owner.PrincipalId,
            "job-host-exact",
            JobExecutionLocations.Host,
            "host-offline",
            [(RemoteHostValidation.SupportedCapabilityId, RemoteHostValidation.SupportedCapabilityVersion)],
            ["repo-main"],
            JobExecutionFallbackPolicies.None,
            1), 0);
        var exactRun = await store.CreateRunOccurrenceAsync(owner.PrincipalId, "job-host-exact", now);
        Assert.NotNull(exactRun);
        var exactFence = await store.AcquireRunLeaseAsync(owner.PrincipalId, exactRun!.RunId, "worker-exact", now, TimeSpan.FromMinutes(10));
        Assert.NotNull(exactFence);
        var exactDispatch = await store.PrepareHostDispatchAsync(exactJob, exactRun, exactFence!.Value, now, TimeSpan.FromMinutes(10));
        Assert.True(exactDispatch.RoutedToHost);
        Assert.Null(exactDispatch.Lease);
        Assert.Equal(JobRunBlockerCodes.WaitingForHost, exactDispatch.Blocker!.Code);
        Assert.Equal("host-offline", exactDispatch.Blocker.HostId);

        var anyJob = new ProductJob(owner.PrincipalId, "job-host-any", "Host any", "Inspect repository", "ACTIVE", "READY", null,
            new JobSchedule("once", now, null, "UTC", null), null, "{}", [], [("host.repo.identity", "1")], [], now, now, 1);
        await store.AddJobAsync(anyJob);
        await store.PutJobExecutionPolicyAsync(new(
            owner.PrincipalId,
            "job-host-any",
            JobExecutionLocations.AnyCompatibleHost,
            null,
            [(RemoteHostValidation.SupportedCapabilityId, RemoteHostValidation.SupportedCapabilityVersion)],
            ["repo-main"],
            JobExecutionFallbackPolicies.None,
            1), 0);
        var anyRun = await store.CreateRunOccurrenceAsync(owner.PrincipalId, "job-host-any", now);
        Assert.NotNull(anyRun);
        var anyFence = await store.AcquireRunLeaseAsync(owner.PrincipalId, anyRun!.RunId, "worker-any", now, TimeSpan.FromMinutes(10));
        Assert.NotNull(anyFence);
        var anyDispatch = await store.PrepareHostDispatchAsync(anyJob, anyRun, anyFence!.Value, now, TimeSpan.FromMinutes(10));
        Assert.True(anyDispatch.RoutedToHost);
        Assert.NotNull(anyDispatch.Lease);
        Assert.Equal("host-online", anyDispatch.Lease!.HostId);
    }

    private static async Task AddGitHubDependencyAsync(SqliteKernelStore store, string owner, DateTimeOffset now)
    {
        await store.AddPluginInstallationAsync(new(owner, "github", "1.0.0", "GitHub", "Tessera",
            "sha256:package", GitHubManifest, "{}", true, now, now, 1));
        await store.AddConnectedAccountAsync(new(owner, "account-1", "github", "github", "1.0.0",
            "GitHub", null, AccountLifecycle.Connected, ConnectedAccountCredentialRef.Create(owner,"account-1"), AccountHealth.Healthy,
            null, "{}", ["issues:write"], [new("github", "1.0.0", "github.issues.create", "1")], now, now, 1));
    }

    private static async Task SetActionStateAsync(
        string databasePath,
        string owner,
        string actionId,
        string state,
        string authorizationRef,
        DateTimeOffset now)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Foreign Keys=True;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE actions SET state=$state,authorization_ref=$authorization,
                started_at=CASE WHEN $state='EXTERNALLY_CONFIRMED' THEN $now ELSE started_at END,
                completed_at=CASE WHEN $state='EXTERNALLY_CONFIRMED' THEN $now ELSE completed_at END,
                attempt_count=CASE WHEN $state='EXTERNALLY_CONFIRMED' THEN 1 ELSE attempt_count END,
                provider_receipt=CASE WHEN $state='EXTERNALLY_CONFIRMED' THEN 'receipt' ELSE provider_receipt END,
                verification_state=CASE WHEN $state='EXTERNALLY_CONFIRMED' THEN 'verified' ELSE verification_state END,
                version=version+1
            WHERE owner_principal_id=$owner AND action_id=$action;
            """;
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$authorization", authorizationRef);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$action", actionId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedOnlineHostAsync(string databasePath, string owner, string hostId, string resourceId)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(false);
        var jwk = RemoteHostValidation.NormalizeP256PublicJwk(
            $$"""{"kty":"EC","crv":"P-256","x":"{{Base64Url(parameters.Q.X!)}}","y":"{{Base64Url(parameters.Q.Y!)}}"}""");
        var timestamp = new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero).ToString("O");
        await using var connection = new SqliteConnection($"Data Source={databasePath};Foreign Keys=True;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $$"""
            INSERT INTO remote_hosts(owner_principal_id,host_id,display_name,platform,architecture,lifecycle,connection_status,public_key_jwk,key_version,protection,agent_version,protocol_version,capability_catalog_version,last_accepted_sequence,last_seen_at,paired_at,revoked_at,version)
            VALUES('{{owner}}','{{hostId}}','{{hostId}}','macOS','arm64','ONLINE','ONLINE','{{jwk.CanonicalJson}}',1,'KEYCHAIN_THIS_DEVICE_ONLY','1.0.0','1',1,0,'{{timestamp}}','{{timestamp}}',NULL,1);
            INSERT INTO host_capability_advertisements(owner_principal_id,host_id,capability_id,capability_version,schema_hash,side_effect_class,advertised_at)
            VALUES('{{owner}}','{{hostId}}','host.repo.identity','1','{{new string('a', 64)}}','READ_ONLY','{{timestamp}}');
            INSERT INTO host_capability_grants(owner_principal_id,host_id,capability_id,capability_version,granted_at,revoked_at,version)
            VALUES('{{owner}}','{{hostId}}','host.repo.identity','1','{{timestamp}}',NULL,1);
            INSERT INTO host_resources(owner_principal_id,host_id,resource_id,type,display_name,fingerprint,state,advertised_at,version)
            VALUES('{{owner}}','{{hostId}}','{{resourceId}}','REPOSITORY','Repo','{{new string('b', 64)}}','AVAILABLE','{{timestamp}}',1);
            INSERT INTO host_resource_grants(owner_principal_id,host_id,resource_id,access_mode,granted_at,revoked_at,version)
            VALUES('{{owner}}','{{hostId}}','{{resourceId}}','READ_ONLY','{{timestamp}}',NULL,1);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetHostLifecycleAsync(string databasePath, string owner, string hostId, string lifecycle)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Foreign Keys=True;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE remote_hosts SET lifecycle=$lifecycle,connection_status=$lifecycle WHERE owner_principal_id=$owner AND host_id=$host;";
        command.Parameters.AddWithValue("$lifecycle", lifecycle);
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$host", hostId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ClearBlockerAsync(
        string databasePath, string owner, string runId, DateTimeOffset clearedAt)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Foreign Keys=True;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE job_run_blockers SET cleared_at=$cleared WHERE owner_principal_id=$owner AND run_id=$run AND cleared_at IS NULL;";
        command.Parameters.AddWithValue("$cleared", clearedAt.ToString("O"));
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$run", runId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<long>> ReadBlockerVersionsAsync(
        string databasePath, string owner, string runId)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM job_run_blockers WHERE owner_principal_id=$owner AND run_id=$run ORDER BY version;";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$run", runId);
        var values = new List<long>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) values.Add(reader.GetInt64(0));
        return values;
    }

    private static string Base64Url(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

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