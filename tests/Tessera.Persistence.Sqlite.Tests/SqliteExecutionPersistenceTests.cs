using Microsoft.Data.Sqlite;
using System.Text.Json;
using Tessera.Core.Kernel;
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
}