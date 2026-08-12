using System.Text.Json;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Xunit;

namespace Tessera.Persistence.Sqlite.Tests;

public sealed class SqliteBackupTests
{
    [Fact]
    public async Task Restore_refuses_and_preserves_an_existing_destination()
    {
        using var database=new TemporaryDatabase();var store=database.CreateStore();await store.InitializeAsync();
        var backup=$"{database.Path}.backup";var destination=$"{database.Path}.existing";var original=new byte[]{1,2,3,4};
        try
        {
            await store.BackupAsync(backup);await File.WriteAllBytesAsync(destination,original);
            await Assert.ThrowsAsync<IOException>(()=>SqliteKernelStore.RestoreBackupAsync(backup,destination));
            Assert.Equal(original,await File.ReadAllBytesAsync(destination));
        }
        finally{Delete(backup);Delete(destination);}
    }

    [Fact]
    public async Task Online_backup_restores_representative_product_state_into_isolated_database()
    {
        using var database=new TemporaryDatabase();var store=database.CreateStore();await store.InitializeAsync();var now=DateTimeOffset.UtcNow;
        var owner=PrincipalRef.Create("https://issuer.example","tenant","subject","owner",now);await store.AddAsync(owner);
        await store.AddConversationAsync(new(owner.PrincipalId,"conversation-1","Dogfood","ACTIVE",null,now,now,1));
        await new R2MemoryService(store,store).RememberAsync(owner.PrincipalId,"user","summary.preference","concise","backup-memory",now);
        var schedule=new JobSchedule("once",now.AddHours(1),null,"UTC",null);await store.AddJobAsync(new(owner.PrincipalId,"job-1","Daily summary","Summarize open work","ACTIVE","READY",null,schedule,schedule.At,"{}",[],[],[],now,now,1));
        const string manifest="""{"Id":"github","Version":"1.0.0","Name":"GitHub","Publisher":"Tessera","MinimumTesseraVersion":"2.0.0","Capabilities":[{"Id":"github.issues.list","Version":"1","Description":"List issues","ExecutorKind":"github-rest","AccountRequired":true,"RequiredPermissions":["issues:read"],"SideEffectClass":"ReadOnly","TimeoutMilliseconds":30000,"MaxResultBytes":32768}]}""";
        await store.AddPluginInstallationAsync(new(owner.PrincipalId,"github","1.0.0","GitHub","Tessera","hash",manifest,"{}",true,now,now,1));
        await store.AddConnectedAccountAsync(new(owner.PrincipalId,"account-1","github","github","1.0.0","Work GitHub","octocat",AccountLifecycle.Connected,ConnectedAccountCredentialRef.Create(owner.PrincipalId,"account-1"),AccountHealth.Healthy,now,JsonSerializer.Serialize(new{allowedRepositories=new[]{"owner/repo"}}),["issues:read"],[new("github","1.0.0","github.issues.list","1")],now,now,1));
        var backup=$"{database.Path}.backup";var restored=$"{database.Path}.restored";
        try
        {
            await store.BackupAsync(backup);var verification=await SqliteKernelStore.VerifyBackupAsync(backup);
            Assert.True(verification.IntegrityOk);Assert.Equal(1,verification.Conversations);Assert.Equal(1,verification.Assertions);Assert.Equal(1,verification.Jobs);Assert.Equal(1,verification.Accounts);
            await store.AddConversationAsync(new(owner.PrincipalId,"conversation-2","After backup","ACTIVE",null,now.AddMinutes(1),now.AddMinutes(1),1));
            await SqliteKernelStore.RestoreBackupAsync(backup,restored);var isolated=new SqliteKernelStore(restored);await isolated.InitializeAsync();
            Assert.Single(await isolated.ListConversationsAsync(owner.PrincipalId));Assert.Single(await isolated.ListMemoryAsync(owner.PrincipalId,false));Assert.Single(await isolated.ListJobsAsync(owner.PrincipalId));Assert.Single(await isolated.ListConnectedAccountsAsync(owner.PrincipalId));
            Assert.Equal(2,(await store.ListConversationsAsync(owner.PrincipalId)).Count);
        }
        finally
        {
            Delete(backup);Delete($"{backup}-wal");Delete($"{backup}-shm");Delete(restored);Delete($"{restored}-wal");Delete($"{restored}-shm");
        }
    }

    private static void Delete(string path){if(File.Exists(path))File.Delete(path);}
}
