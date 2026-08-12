using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Xunit;

namespace Tessera.Persistence.Sqlite.Tests;

public sealed class R2JobHealthTests
{
    [Fact]
    public async Task Job_health_tracks_current_account_and_plugin_availability()
    {
        using var database=new TemporaryDatabase();var store=database.CreateStore();await store.InitializeAsync();var now=DateTimeOffset.UtcNow;
        var owner=PrincipalRef.Create("https://issuer.example","tenant","subject","owner",now);await store.AddAsync(owner);
        const string manifest="""{"Id":"github","Version":"1.0.0","Name":"GitHub","Publisher":"Tessera","MinimumTesseraVersion":"2.0.0","Capabilities":[{"Id":"github.issues.list","Version":"1","Description":"List issues","ExecutorKind":"github-rest","AccountRequired":true,"RequiredPermissions":["issues:read"],"SideEffectClass":"ReadOnly","TimeoutMilliseconds":30000,"MaxResultBytes":32768}]}""";
        await store.AddPluginInstallationAsync(new(owner.PrincipalId,"github","1.0.0","GitHub","Tessera","hash",manifest,"{}",true,now,now,1));
        await store.AddConnectedAccountAsync(new(owner.PrincipalId,"account","github","github","1.0.0","GitHub","octo",AccountLifecycle.Connected,ConnectedAccountCredentialRef.Create(owner.PrincipalId,"account"),AccountHealth.Healthy,now,"{}",["issues:read"],[new("github","1.0.0","github.issues.list","1")],now,now,1));
        var schedule=new JobSchedule("once",now.AddHours(1),null,"UTC",null);await store.AddJobAsync(new(owner.PrincipalId,"job","Issues","List issues","ACTIVE","READY",null,schedule,schedule.At,"{}",["account"],[("github.issues.list","1")],[],now,now,1));

        await store.RecomputeJobsHealthAsync(owner.PrincipalId);Assert.Equal("READY",(await store.GetJobAsync(owner.PrincipalId,"job"))!.Health);
        var account=await store.SetConnectedAccountStateAsync(owner.PrincipalId,"account",1,AccountLifecycle.Disabled,AccountHealth.Unknown);await store.RecomputeJobsHealthAsync(owner.PrincipalId);Assert.Equal("BLOCKED",(await store.GetJobAsync(owner.PrincipalId,"job"))!.Health);
        await store.SetConnectedAccountStateAsync(owner.PrincipalId,"account",account.Version,AccountLifecycle.Connected,AccountHealth.Healthy);await store.RecomputeJobsHealthAsync(owner.PrincipalId);Assert.Equal("READY",(await store.GetJobAsync(owner.PrincipalId,"job"))!.Health);
        var plugin=await store.GetPluginInstallationAsync(owner.PrincipalId,"github","1.0.0");Assert.True(await store.SetPluginEnabledAsync(owner.PrincipalId,"github","1.0.0",plugin!.Version,false));await store.RecomputeJobsHealthAsync(owner.PrincipalId);Assert.Equal("BLOCKED",(await store.GetJobAsync(owner.PrincipalId,"job"))!.Health);
    }
}
