using Microsoft.Data.Sqlite;
using System.Text.Json;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Xunit;

namespace Tessera.Persistence.Sqlite.Tests;

public sealed class SqliteExecutionPersistenceTests
{
    [Fact]
    public async Task Started_action_survives_restart_with_idempotency_and_version()
    {
        using var database = new TemporaryDatabase();
        var principal = KernelTestData.Principal();
        var store = database.CreateStore();
        await store.InitializeAsync();
        await store.AddAsync(principal);
        var proposed = KernelTestData.Action(principal.PrincipalId, capabilityVersion: "2");
        await store.AddAsync(principal.PrincipalId, proposed);
        var authorized = proposed.TransitionTo(
            ActionState.Authorized,
            KernelTestData.T0.AddMinutes(1),
            authorizationRef: "authorization-1");
        Assert.True(await store.UpdateAsync(principal.PrincipalId, authorized, proposed.Version));
        var started = authorized.TransitionTo(ActionState.Started, KernelTestData.T0.AddMinutes(2));
        Assert.True(await store.UpdateAsync(principal.PrincipalId, started, authorized.Version));

        var restarted = database.CreateStore();
        await restarted.InitializeAsync();
        var loaded = await restarted.GetActionAsync(principal.PrincipalId, proposed.ActionId);

        Assert.NotNull(loaded);
        Assert.Equal(ActionState.Started, loaded.State);
        Assert.Equal("idempotency-1", loaded.IdempotencyKey);
        Assert.Equal("2", loaded.CapabilityVersion);
        Assert.Equal(1, loaded.AttemptCount);
    }

    [Fact]
    public async Task Failed_action_retries_explicitly_with_same_idempotency_key()
    {
        using var database = new TemporaryDatabase();
        var principal = KernelTestData.Principal();
        var store = database.CreateStore();
        await store.InitializeAsync();
        await store.AddAsync(principal);
        var proposed = KernelTestData.Action(principal.PrincipalId);
        await store.AddAsync(principal.PrincipalId, proposed);
        var authorized = proposed.TransitionTo(ActionState.Authorized, KernelTestData.T0, authorizationRef: "auth-1");
        var started = authorized.TransitionTo(ActionState.Started, KernelTestData.T0.AddMinutes(1));
        var failed = started.TransitionTo(ActionState.Failed, KernelTestData.T0.AddMinutes(2), failure: "timeout");
        var retried = failed.TransitionTo(ActionState.Started, KernelTestData.T0.AddMinutes(3));
        Assert.True(await store.UpdateAsync(principal.PrincipalId, authorized, proposed.Version));
        Assert.True(await store.UpdateAsync(principal.PrincipalId, started, authorized.Version));
        Assert.True(await store.UpdateAsync(principal.PrincipalId, failed, started.Version));
        Assert.True(await store.UpdateAsync(principal.PrincipalId, retried, failed.Version));

        var loaded = await store.GetActionAsync(principal.PrincipalId, proposed.ActionId);
        Assert.NotNull(loaded);
        Assert.Equal(ActionState.Started, loaded.State);
        Assert.Equal(2, loaded.AttemptCount);
        Assert.Equal(proposed.IdempotencyKey, loaded.IdempotencyKey);
    }

    [Fact]
    public async Task Stale_action_update_is_rejected_optimistically()
    {
        using var database = new TemporaryDatabase();
        var principal = KernelTestData.Principal();
        var store = database.CreateStore();
        await store.InitializeAsync();
        await store.AddAsync(principal);
        var proposed = KernelTestData.Action(principal.PrincipalId);
        await store.AddAsync(principal.PrincipalId, proposed);
        var authorized = proposed.TransitionTo(ActionState.Authorized, KernelTestData.T0, authorizationRef: "auth-1");
        Assert.True(await store.UpdateAsync(principal.PrincipalId, authorized, proposed.Version));

        var competing = proposed.TransitionTo(ActionState.Canceled, KernelTestData.T0.AddMinutes(1));
        Assert.False(await store.UpdateAsync(principal.PrincipalId, competing, proposed.Version));
    }

    [Fact]
    public async Task Action_update_cannot_swap_immutable_payload_binding()
    {
        using var database = new TemporaryDatabase();
        var principal = KernelTestData.Principal();
        var store = database.CreateStore();
        await store.InitializeAsync();
        await store.AddAsync(principal);
        var proposed = KernelTestData.Action(principal.PrincipalId);
        await store.AddAsync(principal.PrincipalId, proposed);
        var swapped = ActionRecord.Create(
            proposed.ActionId,
            proposed.OwnerPrincipalId,
            proposed.CapabilityId,
            proposed.CapabilityVersion,
            proposed.Intent,
            ActionPayloadHash.Compute("swapped"u8),
            proposed.TargetScope,
            proposed.RiskClass,
            proposed.PolicyDecisionRef,
            "auth-1",
            ActionState.Authorized,
            proposed.IdempotencyKey,
            proposed.AttemptCount,
            proposed.CreatedAt,
            null,
            null,
            null,
            null,
            null,
            proposed.SchemaVersion,
            proposed.Version + 1);

        Assert.False(await store.UpdateAsync(principal.PrincipalId, swapped, proposed.Version));
        var loaded = await store.GetActionAsync(principal.PrincipalId, proposed.ActionId);
        Assert.NotNull(loaded);
        Assert.Equal(proposed.PayloadHash, loaded.PayloadHash);
        Assert.Equal(ActionState.Proposed, loaded.State);
    }

    [Fact]
    public async Task Duplicate_idempotency_key_is_rejected_for_same_owner()
    {
        using var database = new TemporaryDatabase();
        var principal = KernelTestData.Principal();
        var store = database.CreateStore();
        await store.InitializeAsync();
        await store.AddAsync(principal);
        await store.AddAsync(principal.PrincipalId, KernelTestData.Action(principal.PrincipalId, "action-1"));

        await Assert.ThrowsAsync<SqliteException>(() => store.AddAsync(
            principal.PrincipalId,
            KernelTestData.Action(principal.PrincipalId, "action-2")));
    }

    [Fact]
    public async Task Store_rejects_non_proposed_insert_and_transition_jump()
    {
        using var database = new TemporaryDatabase();
        var principal = KernelTestData.Principal();
        var store = database.CreateStore();
        await store.InitializeAsync();
        await store.AddAsync(principal);
        var proposed = KernelTestData.Action(principal.PrincipalId);
        var authorized = proposed.TransitionTo(ActionState.Authorized, KernelTestData.T0, "auth-1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AddAsync(principal.PrincipalId, authorized));

        await store.AddAsync(principal.PrincipalId, proposed);
        var verified = ActionRecord.Create(
            proposed.ActionId,
            proposed.OwnerPrincipalId,
            proposed.CapabilityId,
            proposed.CapabilityVersion,
            proposed.Intent,
            proposed.PayloadHash,
            proposed.TargetScope,
            proposed.RiskClass,
            proposed.PolicyDecisionRef,
            "auth-1",
            ActionState.ProviderVerified,
            proposed.IdempotencyKey,
            1,
            proposed.CreatedAt,
            KernelTestData.T0.AddMinutes(1),
            null,
            "accepted",
            "matched",
            null,
            proposed.SchemaVersion,
            1);

        Assert.False(await store.UpdateAsync(principal.PrincipalId, verified, proposed.Version));
        Assert.Equal(ActionState.Proposed, (await store.GetActionAsync(principal.PrincipalId, proposed.ActionId))!.State);
    }

    [Fact]
    public async Task Authorization_binding_and_consumption_survive_restart()
    {
        using var database = new TemporaryDatabase();
        var principal = KernelTestData.Principal();
        var store = database.CreateStore();
        await store.InitializeAsync();
        await store.AddAsync(principal);
        var action = KernelTestData.Action(principal.PrincipalId);
        await store.AddAsync(principal.PrincipalId, action);
        var service = new ActionAuthorizationService(store);
        var authorization = await service.IssueAsync(
            action,
            KernelTestData.T0,
            KernelTestData.T0.AddMinutes(10));

        var restartedService = new ActionAuthorizationService(database.CreateStore());
        Assert.Null(await AuthorizeAsync(
            restartedService,
            action,
            authorization,
            payloadHash: ActionPayloadHash.Compute("swapped"u8),
            now: KernelTestData.T0.AddMinutes(1)));
        var authorized = await AuthorizeAsync(
            restartedService,
            action,
            authorization,
            now: KernelTestData.T0.AddMinutes(1));
        Assert.NotNull(authorized);
        Assert.Equal(ActionState.Authorized, authorized.State);
        Assert.Null(await AuthorizeAsync(
            restartedService,
            action,
            authorization,
            now: KernelTestData.T0.AddMinutes(2)));

        var expired = await service.IssueAsync(
            action,
            KernelTestData.T0,
            KernelTestData.T0.AddMinutes(1));
        Assert.Null(await AuthorizeAsync(
            restartedService,
            action,
            expired,
            now: KernelTestData.T0.AddMinutes(1)));

        var persisted = await database.CreateStore().GetActionAsync(principal.PrincipalId, action.ActionId);
        Assert.NotNull(persisted);
        Assert.Equal(ActionState.Authorized, persisted.State);
        Assert.Equal(authorization.AuthorizationId, persisted.AuthorizationRef);
    }

    [Fact]
    public async Task Host_bound_authorization_consumption_requires_exact_host_lease_and_resource_hash_match()
    {
        using var database = new TemporaryDatabase();
        var principal = KernelTestData.Principal();
        var store = database.CreateStore();
        await store.InitializeAsync();
        await store.AddAsync(principal);

        var resourceHash = RemoteHostValidation.ComputeHostResourceGrantHash(
        [
            new HostResourceGrantTuple("repo-main", 1, "READ_ONLY", new string('b', 64)),
        ]);

        var binding = new ActionR2Binding(
            null,
            "local",
            "1.0.0",
            new string('a', 64),
            KernelTestData.T0.AddMinutes(10),
            "execution-1",
            jobRunId: "run-host",
            hostId: "host-main",
            hostLeaseId: "lease-main",
            hostResourceGrantHash: resourceHash);
        await SeedActiveHostLeaseAsync(database.Path, principal.PrincipalId, binding, KernelTestData.T0);
        var action = HostAction(principal.PrincipalId).BindR2(binding);
        await store.AddAsync(principal.PrincipalId, action);
        var service = new ActionAuthorizationService(store);
        var authorization = await service.IssueAsync(action, KernelTestData.T0, KernelTestData.T0.AddMinutes(10));

        var restartedService = new ActionAuthorizationService(database.CreateStore());
        Assert.Null(await restartedService.AuthorizeAsync(
            action with { R2Binding = binding with { HostId = "host-other" } },
            authorization.AuthorizationId,
            KernelTestData.T0.AddMinutes(1)));
        Assert.Null(await restartedService.AuthorizeAsync(
            action with { R2Binding = binding with { HostLeaseId = "lease-other" } },
            authorization.AuthorizationId,
            KernelTestData.T0.AddMinutes(1)));
        Assert.Null(await restartedService.AuthorizeAsync(
            action with { R2Binding = binding with { HostResourceGrantHash = new string('c', 64) } },
            authorization.AuthorizationId,
            KernelTestData.T0.AddMinutes(1)));

        var authorized = await restartedService.AuthorizeAsync(
            action,
            authorization.AuthorizationId,
            KernelTestData.T0.AddMinutes(1));
        Assert.NotNull(authorized);
        Assert.Equal("host-main", authorized!.R2Binding?.HostId);
        Assert.Equal("lease-main", authorized.R2Binding?.HostLeaseId);
        Assert.Equal(resourceHash, authorized.R2Binding?.HostResourceGrantHash);
    }

    [Fact]
    public async Task Host_bound_authorization_consumption_rejects_grant_drift()
    {
        using var database = new TemporaryDatabase();
        var principal = KernelTestData.Principal();
        var store = database.CreateStore();
        await store.InitializeAsync();
        await store.AddAsync(principal);

        var resourceHash = RemoteHostValidation.ComputeHostResourceGrantHash(
        [
            new HostResourceGrantTuple("repo-main", 1, "READ_ONLY", new string('b', 64)),
        ]);
        var binding = new ActionR2Binding(
            null,
            "local",
            "1.0.0",
            new string('a', 64),
            KernelTestData.T0.AddMinutes(10),
            "execution-drift",
            jobRunId: "run-drift",
            hostId: "host-drift",
            hostLeaseId: "lease-drift",
            hostResourceGrantHash: resourceHash);
        await SeedActiveHostLeaseAsync(database.Path, principal.PrincipalId, binding, KernelTestData.T0);

        var action = HostAction(principal.PrincipalId, "action-drift").BindR2(binding);
        await store.AddAsync(principal.PrincipalId, action);
        var service = new ActionAuthorizationService(store);
        var authorization = await service.IssueAsync(action, KernelTestData.T0, KernelTestData.T0.AddMinutes(10));
        await RevokeSeededHostResourceGrantAsync(database.Path, principal.PrincipalId, binding.HostId!, KernelTestData.T0.AddMinutes(1));

        Assert.Null(await new ActionAuthorizationService(database.CreateStore()).AuthorizeAsync(
            action,
            authorization.AuthorizationId,
            KernelTestData.T0.AddMinutes(1)));
    }

    [Fact]
    public async Task Host_bound_action_start_rejects_terminal_lease()
    {
        using var database = new TemporaryDatabase();
        var principal = KernelTestData.Principal();
        var store = database.CreateStore();
        await store.InitializeAsync();
        await store.AddAsync(principal);

        var resourceHash = RemoteHostValidation.ComputeHostResourceGrantHash(
        [
            new HostResourceGrantTuple("repo-main", 1, "READ_ONLY", new string('b', 64)),
        ]);
        var binding = new ActionR2Binding(
            null,
            "local",
            "1.0.0",
            new string('a', 64),
            KernelTestData.T0.AddMinutes(10),
            "execution-terminal",
            jobRunId: "run-terminal",
            hostId: "host-terminal",
            hostLeaseId: "lease-terminal",
            hostResourceGrantHash: resourceHash);
        await SeedActiveHostLeaseAsync(database.Path, principal.PrincipalId, binding, KernelTestData.T0);

        var action = HostAction(principal.PrincipalId, "action-terminal").BindR2(binding);
        await store.AddAsync(principal.PrincipalId, action);
        var authorizationService = new ActionAuthorizationService(store);
        var authorization = await authorizationService.IssueAsync(action, KernelTestData.T0, KernelTestData.T0.AddMinutes(10));
        var authorized = await authorizationService.AuthorizeAsync(action, authorization.AuthorizationId, KernelTestData.T0.AddMinutes(1));
        Assert.NotNull(authorized);

        await CompleteSeededHostLeaseAsync(database.Path, principal.PrincipalId, binding.HostLeaseId!, binding.JobRunId!, KernelTestData.T0.AddMinutes(2));

        var input = JsonDocument.Parse("{}" ).RootElement.Clone();
        var capability = new DeterministicCapability(
            CapabilityDescriptor.Create(
                action.CapabilityId,
                action.CapabilityVersion,
                "Fake external capability",
                "{}",
                "{}",
                SideEffectClass.ExternalReversible,
                ["calendar.write"],
                [SensitivityClass.Internal],
                IdempotencySupport.Keyed,
                VerificationSupport.ProviderState),
            _ => new CapabilityResult(CapabilityOutcome.Succeeded, JsonDocument.Parse("{}").RootElement.Clone(), null, null, null));
        var execution = new ActionExecutionService(store);
        var invocation = new CapabilityInvocation(
            principal.PrincipalId,
            "workflow-terminal",
            action.CapabilityId,
            action.CapabilityVersion,
            action.TargetScope,
            input,
            authorization.AuthorizationId,
            action.IdempotencyKey);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await execution.InvokeAsync(
            action.ActionId,
            authorized!.Version,
            capability,
            invocation,
            KernelTestData.T0.AddMinutes(3)));
        var expired = await store.GetActionAsync(principal.PrincipalId, action.ActionId);
        Assert.Equal(ActionState.Expired, expired!.State);
        Assert.Equal("host_lease_invalidated", expired.Failure);
    }

    [Fact]
    public async Task Execution_reservation_rejects_target_swap_and_stale_replay()
    {
        using var database = new TemporaryDatabase();
        var principal = KernelTestData.Principal();
        var store = database.CreateStore();
        await store.InitializeAsync();
        await store.AddAsync(principal);
        var input = JsonDocument.Parse("{\"start\":\"16:30\"}").RootElement.Clone();
        var action = KernelTestData.Action(principal.PrincipalId);
        await store.AddAsync(principal.PrincipalId, action);
        var authorizationService = new ActionAuthorizationService(store);
        var authorization = await authorizationService.IssueAsync(
            action,
            KernelTestData.T0,
            KernelTestData.T0.AddMinutes(10));
        var authorized = await authorizationService.AuthorizeAsync(
            action,
            authorization.AuthorizationId,
            KernelTestData.T0.AddMinutes(1));
        Assert.NotNull(authorized);
        var invoked = 0;
        var capability = new DeterministicCapability(
            CapabilityDescriptor.Create(
                action.CapabilityId,
                action.CapabilityVersion,
                "Fake external capability",
                "{}",
                "{}",
                SideEffectClass.ExternalReversible,
                ["calendar.write"],
                [SensitivityClass.Internal],
                IdempotencySupport.Keyed,
                VerificationSupport.ProviderState),
            _ =>
            {
                invoked++;
                return new CapabilityResult(
                    CapabilityOutcome.Succeeded,
                    JsonDocument.Parse("{}").RootElement.Clone(),
                    null,
                    null,
                    null);
            });
        var execution = new ActionExecutionService(store);
        var swappedTarget = new CapabilityInvocation(
            principal.PrincipalId,
            "workflow-1",
            action.CapabilityId,
            action.CapabilityVersion,
            "calendar/other",
            input,
            authorization.AuthorizationId,
            action.IdempotencyKey);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await execution.InvokeAsync(
            action.ActionId,
            authorized.Version,
            capability,
            swappedTarget,
            KernelTestData.T0.AddMinutes(2)));

        var exact = swappedTarget with { TargetScope = action.TargetScope };
        await execution.InvokeAsync(
            action.ActionId,
            authorized.Version,
            capability,
            exact,
            KernelTestData.T0.AddMinutes(2));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () => await execution.InvokeAsync(
            action.ActionId,
            authorized.Version,
            capability,
            exact,
            KernelTestData.T0.AddMinutes(3)));
        Assert.Equal(1, invoked);
        Assert.Equal(
            ActionState.Started,
            (await store.GetActionAsync(principal.PrincipalId, action.ActionId))!.State);
    }

    [Fact]
    public async Task Workflow_checkpoint_survives_restart_and_rejects_stale_update()
    {
        using var database = new TemporaryDatabase();
        var principal = KernelTestData.Principal();
        var store = database.CreateStore();
        await store.InitializeAsync();
        await store.AddAsync(principal);
        IWorkflowRepository workflows = store;
        var initial = KernelTestData.Workflow(principal.PrincipalId);
        await workflows.AddAsync(principal.PrincipalId, initial);
        var updated = KernelTestData.Workflow(principal.PrincipalId, version: 1);
        Assert.True(await workflows.UpdateAsync(principal.PrincipalId, updated, expectedVersion: 0));
        Assert.False(await workflows.UpdateAsync(principal.PrincipalId, updated, expectedVersion: 0));

        IWorkflowRepository restarted = database.CreateStore();
        var loaded = await restarted.GetAsync(principal.PrincipalId, initial.WorkflowId);
        Assert.NotNull(loaded);
        Assert.Equal("ACTION_STARTED", loaded.State);
        Assert.Equal(1, loaded.Version);
    }

    private static Task<ActionRecord?> AuthorizeAsync(
        ActionAuthorizationService service,
        ActionRecord proposedAction,
        ActionAuthorization authorization,
        string? payloadHash = null,
        DateTimeOffset? now = null)
        => service.AuthorizeAsync(
            payloadHash is null
                ? proposedAction
                : ActionRecord.Create(
                    proposedAction.ActionId,
                    proposedAction.OwnerPrincipalId,
                    proposedAction.CapabilityId,
                    proposedAction.CapabilityVersion,
                    proposedAction.Intent,
                    payloadHash,
                    proposedAction.TargetScope,
                    proposedAction.RiskClass,
                    proposedAction.PolicyDecisionRef,
                    null,
                    ActionState.Proposed,
                    proposedAction.IdempotencyKey,
                    proposedAction.AttemptCount,
                    proposedAction.CreatedAt,
                    null,
                    null,
                    null,
                    null,
                    null,
                    proposedAction.SchemaVersion,
                    proposedAction.Version),
            authorization.AuthorizationId,
            now ?? KernelTestData.T0);

    private static async Task SeedActiveHostLeaseAsync(
        string databasePath,
        string owner,
        ActionR2Binding binding,
        DateTimeOffset now)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Foreign Keys=True;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $$"""
            INSERT INTO remote_hosts(owner_principal_id,host_id,display_name,platform,architecture,lifecycle,connection_status,public_key_jwk,key_version,protection,agent_version,protocol_version,capability_catalog_version,last_accepted_sequence,last_seen_at,paired_at,revoked_at,version)
            VALUES('{{owner}}','{{binding.HostId}}','Host Main','macOS','arm64','ONLINE','ONLINE','{"crv":"P-256","kty":"EC","x":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","y":"BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"}',1,'KEYCHAIN_THIS_DEVICE_ONLY','1.0.0','1',1,0,'{{now:O}}','{{now:O}}',NULL,1);
            INSERT INTO host_capability_advertisements(owner_principal_id,host_id,capability_id,capability_version,schema_hash,side_effect_class,advertised_at)
            VALUES('{{owner}}','{{binding.HostId}}','host.repo.identity','1','{{new string('a', 64)}}','READ_ONLY','{{now:O}}');
            INSERT INTO host_capability_grants(owner_principal_id,host_id,capability_id,capability_version,granted_at,revoked_at,version)
            VALUES('{{owner}}','{{binding.HostId}}','host.repo.identity','1','{{now:O}}',NULL,1);
            INSERT INTO host_resources(owner_principal_id,host_id,resource_id,type,display_name,fingerprint,state,advertised_at,version)
            VALUES('{{owner}}','{{binding.HostId}}','repo-main','REPOSITORY','Repo','{{new string('b', 64)}}','AVAILABLE','{{now:O}}',1);
            INSERT INTO host_resource_grants(owner_principal_id,host_id,resource_id,access_mode,granted_at,revoked_at,version)
            VALUES('{{owner}}','{{binding.HostId}}','repo-main','READ_ONLY','{{now:O}}',NULL,1);
            INSERT INTO jobs(owner_principal_id,job_id,name,instruction,desired_state,health,model_profile_id,schedule_json,next_occurrence,context_policy_json,created_at,updated_at,version)
            VALUES('{{owner}}','job-host','Host job','Inspect repo','ACTIVE','READY',NULL,'{"kind":"once","at":"{{now:O}}","localTime":null,"timeZone":"UTC","days":null}',NULL,'{}','{{now:O}}','{{now:O}}',1);
            INSERT INTO job_runs(owner_principal_id,run_id,job_id,scheduled_for,state,fence,version,started_at,ended_at,model_profile_id,context_snapshot_ref,error_code)
            VALUES('{{owner}}','{{binding.JobRunId}}','job-host','{{now:O}}','QUEUED',0,1,NULL,NULL,NULL,NULL,NULL);
            INSERT INTO scheduler_leases(owner_principal_id,run_id,holder_id,acquired_at,expires_at,fence)
            VALUES('{{owner}}','{{binding.JobRunId}}','worker-host','{{now:O}}','{{now.AddMinutes(5):O}}',11);
            INSERT INTO host_work_leases(owner_principal_id,lease_id,run_id,job_id,host_id,scheduler_fence,attempt,profile_id,capability_id,capability_version,capability_grant_version,input_hash,state,issued_at,execute_until,acknowledged_at,completed_at,local_attempt_id,outcome,output_sha256,failure_code,version)
            VALUES('{{owner}}','{{binding.HostLeaseId}}','{{binding.JobRunId}}','job-host','{{binding.HostId}}',11,1,'host.repo.identity@1','host.repo.identity','1',1,'{{new string('c', 64)}}','OFFERED','{{now:O}}','{{now.AddMinutes(5):O}}',NULL,NULL,NULL,NULL,NULL,NULL,1);
            INSERT INTO host_lease_resources(owner_principal_id,lease_id,resource_id,resource_grant_version,access_mode,fingerprint)
            VALUES('{{owner}}','{{binding.HostLeaseId}}','repo-main',1,'READ_ONLY','{{new string('b', 64)}}');
            UPDATE host_work_leases SET state='ACKNOWLEDGED',acknowledged_at='{{now:O}}',local_attempt_id='attempt-host'
            WHERE owner_principal_id='{{owner}}' AND lease_id='{{binding.HostLeaseId}}';
            UPDATE job_runs SET state='RUNNING',started_at='{{now:O}}',fence=11
            WHERE owner_principal_id='{{owner}}' AND run_id='{{binding.JobRunId}}';
            UPDATE remote_hosts SET lifecycle='BUSY',connection_status='BUSY'
            WHERE owner_principal_id='{{owner}}' AND host_id='{{binding.HostId}}';
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static ActionRecord HostAction(string owner, string actionId = "action-host")
        => ActionRecord.Create(
            actionId,
            owner,
            RemoteHostValidation.SupportedCapabilityId,
            RemoteHostValidation.SupportedCapabilityVersion,
            "inspect repository identity",
            ActionPayloadHash.Compute(System.Text.Encoding.UTF8.GetBytes("{}")),
            "repository/repo-main",
            "ReadOnly",
            "policy-host",
            null,
            ActionState.Proposed,
            $"idempotency-{actionId}",
            0,
            KernelTestData.T0,
            null,
            null,
            null,
            null,
            null,
            1,
            0);

    private static async Task RevokeSeededHostResourceGrantAsync(
        string databasePath,
        string owner,
        string hostId,
        DateTimeOffset revokedAt)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Foreign Keys=True;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE host_resource_grants SET revoked_at=$revokedAt WHERE owner_principal_id=$owner AND host_id=$host AND resource_id='repo-main' AND version=1;";
        command.Parameters.AddWithValue("$revokedAt", revokedAt.ToString("O"));
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$host", hostId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CompleteSeededHostLeaseAsync(
        string databasePath,
        string owner,
        string leaseId,
        string runId,
        DateTimeOffset completedAt)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Foreign Keys=True;Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $$"""
            UPDATE host_work_leases
            SET state='COMPLETED',completed_at='{{completedAt:O}}',outcome='SUCCEEDED',version=version+1
            WHERE owner_principal_id='{{owner}}' AND lease_id='{{leaseId}}';
            UPDATE job_runs
            SET state='SUCCEEDED',ended_at='{{completedAt:O}}',version=version+1
            WHERE owner_principal_id='{{owner}}' AND run_id='{{runId}}';
            """;
        await command.ExecuteNonQueryAsync();
    }
}