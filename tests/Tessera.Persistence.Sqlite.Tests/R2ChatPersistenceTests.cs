using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Xunit;

namespace Tessera.Persistence.Sqlite.Tests;

public sealed class R2ChatPersistenceTests
{
    [Fact]
    public async Task User_prompt_and_public_events_survive_outage_and_restart()
    {
        using var database=new TemporaryDatabase(); var store=database.CreateStore(); await store.InitializeAsync(); var now=DateTimeOffset.UtcNow;
        var owner=PrincipalRef.Create("https://issuer.example","tenant","subject","owner",now); await store.AddAsync(owner);
        await store.AddConversationAsync(new(owner.PrincipalId,"c1","New chat","ACTIVE",null,now,now,1));
        await store.AddMessageAsync(new(owner.PrincipalId,"m1","c1","USER","PERSISTED",null,[new("p1",1,"TEXT","hello")],now,null,1));
        await store.AddMessageAsync(new(owner.PrincipalId,"m2","c1","ASSISTANT","FAILED",null,[new("p2",1,"FAILURE",null,ErrorCode:"provider_unavailable")],now,now,1));
        await store.AddExecutionEventAsync(new(owner.PrincipalId,"e1","x1",1,"failure",now,"m2",null,null,"{\"code\":\"provider_unavailable\",\"retryable\":true}"));
        var restarted=database.CreateStore(); await restarted.InitializeAsync();
        Assert.Equal(2,(await restarted.ListMessagesAsync(owner.PrincipalId,"c1")).Count);
        Assert.Single(await restarted.ListExecutionEventsAsync(owner.PrincipalId,"x1",0));
        Assert.Empty(await restarted.ListExecutionEventsAsync(owner.PrincipalId,"x1",1));
        Assert.Empty(await restarted.ListConversationsAsync("other"));
    }
}