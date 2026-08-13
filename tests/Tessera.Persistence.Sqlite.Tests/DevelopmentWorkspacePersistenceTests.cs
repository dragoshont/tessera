using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Xunit;

namespace Tessera.Persistence.Sqlite.Tests;

public sealed class DevelopmentWorkspacePersistenceTests
{
    [Fact]
    public async Task Workspace_and_atomic_task_creation_are_owner_conversation_scoped_and_idempotent()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var owner = PrincipalRef.Create("https://issuer.example", "tenant", "owner", "owner", now);
        var other = PrincipalRef.Create("https://issuer.example", "tenant", "other", "other", now);
        await store.AddAsync(owner);
        await store.AddAsync(other);
        await store.AddConversationAsync(new(owner.PrincipalId, "conversation-1", "Development", "ACTIVE", null, now, now, 1));
        await store.AddConversationAsync(new(other.PrincipalId, "conversation-1", "Other", "ACTIVE", null, now, now, 1));
        await store.RegisterDevelopmentWorkspaceAsync(new(owner.PrincipalId, "workspace-1", "conversation-1",
            "Repository", "snapshot/one", "sha256:snapshot", "READY", now, 1));
        Assert.Single(await store.ListDevelopmentWorkspacesAsync(owner.PrincipalId, "conversation-1"));
        Assert.Empty(await store.ListDevelopmentWorkspacesAsync(other.PrincipalId, "conversation-1"));
        Assert.True(DevelopmentCommandProfiles.TryResolve("repository.status", [], out var profile));
        var hash = DevelopmentCommandProfiles.CanonicalRequestHash("Status", "workspace-1", profile!.Id, []);

        var secondStore = database.CreateStore();
        await secondStore.InitializeAsync();
        var attempts = await Task.WhenAll(
            store.CreateDevelopmentTaskAsync(owner.PrincipalId, "conversation-1", "key-1", hash,
                "job-1", "run-1", "Status", "workspace-1", profile, "executor@sha256:digest", now),
            secondStore.CreateDevelopmentTaskAsync(owner.PrincipalId, "conversation-1", "key-1", hash,
                "job-1", "run-1", "Status", "workspace-1", profile, "executor@sha256:digest", now));
        Assert.All(attempts, item => Assert.Null(item.ErrorCode));
        Assert.Single(attempts, item => item.Replayed && item.Creation is null);
        Assert.Single(attempts, item => !item.Replayed && item.Creation is not null);
        Assert.Single(attempts.Select(item => item.ResponseBodyJson).Distinct(StringComparer.Ordinal));
        Assert.Single(await store.ListJobsAsync(owner.PrincipalId));
        Assert.Single(await store.ListJobRunsAsync(owner.PrincipalId, "job-1"));

        var conflict = await store.CreateDevelopmentTaskAsync(owner.PrincipalId, "conversation-1", "key-1",
            DevelopmentCommandProfiles.CanonicalRequestHash("Changed", "workspace-1", profile.Id, []),
            "job-1", "run-1", "Changed", "workspace-1", profile, "executor@sha256:digest", now);
        Assert.Equal("idempotency_conflict", conflict.ErrorCode);
        var crossOwner = await store.CreateDevelopmentTaskAsync(other.PrincipalId, "conversation-1", "key-2", hash,
            "job-2", "run-2", "Status", "workspace-1", profile, "executor@sha256:digest", now);
        Assert.Equal("not_found", crossOwner.ErrorCode);
    }

    [Fact]
    public async Task Revoked_workspace_is_unavailable_and_stale_fence_cannot_persist_output_or_event()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var owner = PrincipalRef.Create("https://issuer.example", "tenant", "owner", "owner", now);
        await store.AddAsync(owner);
        await store.AddConversationAsync(new(owner.PrincipalId, "conversation-1", "Development", "ACTIVE", null, now, now, 1));
        var workspace = new DevelopmentWorkspace(owner.PrincipalId, "workspace-1", "conversation-1", "Repository",
            "snapshot/one", "sha256:snapshot", "READY", now, 1);
        await store.RegisterDevelopmentWorkspaceAsync(workspace);
        Assert.True(DevelopmentCommandProfiles.TryResolve("repository.status", [], out var profile));
        var hash = DevelopmentCommandProfiles.CanonicalRequestHash("Status", "workspace-1", profile!.Id, []);
        var created = await store.CreateDevelopmentTaskAsync(owner.PrincipalId, "conversation-1", "key-1", hash,
            "job-1", "run-1", "Status", "workspace-1", profile, "executor@sha256:digest", now);
        Assert.NotNull(created.Creation);
        var fence1 = await store.AcquireRunLeaseAsync(owner.PrincipalId, "run-1", "worker-1", now, TimeSpan.FromSeconds(1));
        Assert.True(await store.StartRunAsync(owner.PrincipalId, "run-1", 1, fence1!.Value, now));
        Assert.Equal(1, await store.RecoverExpiredRunningRunsAsync(now.AddSeconds(2)));
        var fence2 = await store.AcquireRunLeaseAsync(owner.PrincipalId, "run-1", "worker-2", now.AddSeconds(2), TimeSpan.FromMinutes(1));
        Assert.NotNull(fence2);
        var recovered = await store.GetJobRunAsync(owner.PrincipalId, "run-1");
        Assert.True(await store.StartRunAsync(owner.PrincipalId, "run-1", recovered!.Version, fence2.Value, now.AddSeconds(2)));
        var output = new NormalizedDevelopmentOutput("bounded", false);
        Assert.False(await store.CompleteDevelopmentRunAsync(owner.PrincipalId, "conversation-1", "job-1", "run-1",
            fence1.Value, "SUCCEEDED", null, output, now.AddSeconds(2)));
        Assert.Empty(await store.ListJobRunOutputsAsync(owner.PrincipalId, "run-1"));
        Assert.Empty(await store.ListMessagesAsync(owner.PrincipalId, "conversation-1"));
        Assert.True(await store.CompleteDevelopmentRunAsync(owner.PrincipalId, "conversation-1", "job-1", "run-1",
            fence2.Value, "SUCCEEDED", null, output, now.AddSeconds(2)));
        Assert.Single(await store.ListJobRunOutputsAsync(owner.PrincipalId, "run-1"));
        var message = Assert.Single(await store.ListMessagesAsync(owner.PrincipalId, "conversation-1"));
        Assert.Equal("SYSTEM_EVENT", message.Role);
        Assert.Contains("output:run-1:log", Assert.Single(message.Parts).Text, StringComparison.Ordinal);

        var cancelHash = DevelopmentCommandProfiles.CanonicalRequestHash("Cancel", "workspace-1", profile.Id, []);
        var canceledTask = await store.CreateDevelopmentTaskAsync(owner.PrincipalId, "conversation-1", "cancel-key", cancelHash,
            "job-cancel", "run-cancel", "Cancel", "workspace-1", profile, "executor@sha256:digest", now.AddSeconds(3));
        Assert.NotNull(canceledTask.Creation);
        var cancelFence = await store.AcquireRunLeaseAsync(owner.PrincipalId, "run-cancel", "worker-3", now.AddSeconds(3), TimeSpan.FromMinutes(1));
        Assert.True(await store.StartRunAsync(owner.PrincipalId, "run-cancel", 1, cancelFence!.Value, now.AddSeconds(3)));
        Assert.True(await store.SetJobDesiredStateAsync(owner.PrincipalId, "job-cancel", 1, "CANCELED"));
        Assert.True(await store.CompleteDevelopmentRunAsync(owner.PrincipalId, "conversation-1", "job-cancel", "run-cancel",
            cancelFence.Value, "SUCCEEDED", null, output, now.AddSeconds(4)));
        var canceledRun = await store.GetJobRunAsync(owner.PrincipalId, "run-cancel");
        Assert.Equal("CANCELED", canceledRun!.State);
        Assert.Equal("job_canceled", canceledRun.ErrorCode);

        await store.RegisterDevelopmentWorkspaceAsync(workspace with { State = "REVOKED" });
        var unavailable = await store.CreateDevelopmentTaskAsync(owner.PrincipalId, "conversation-1", "key-2", hash,
            "job-2", "run-2", "Status", "workspace-1", profile, "executor@sha256:digest", now);
        Assert.Equal("workspace_unavailable", unavailable.ErrorCode);
    }
}