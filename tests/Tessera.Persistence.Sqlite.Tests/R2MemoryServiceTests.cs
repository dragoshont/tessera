using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Xunit;

namespace Tessera.Persistence.Sqlite.Tests;

public sealed class R2MemoryServiceTests
{
    [Fact]
    public async Task Remember_correct_and_why_preserve_user_evidence_across_restart()
    {
        using var database=new TemporaryDatabase(); var store=database.CreateStore(); await store.InitializeAsync(); var now=DateTimeOffset.UtcNow;
        var owner=PrincipalRef.Create("https://issuer.example","tenant","subject","owner",now); await store.AddAsync(owner);
        var service=new R2MemoryService(store,store); var morning=await service.RememberAsync(owner.PrincipalId,"user","appointment.preference","morning","m1",now);
        var afternoon=await service.CorrectAsync(owner.PrincipalId,morning.AssertionId,"afternoon","m2",now.AddMinutes(1));
        var restarted=database.CreateStore(); await restarted.InitializeAsync(); var why=await new R2MemoryService(restarted,restarted).WhyAsync(owner.PrincipalId,afternoon.AssertionId);
        Assert.Equal("afternoon",why.Current.Value); Assert.Equal(2,why.History.Count); Assert.Equal(2,why.Evidence.Count);
        Assert.All(why.History,item=>Assert.Equal(AssertionType.UserAsserted,item.AssertionType));
        await Assert.ThrowsAsync<KeyNotFoundException>(()=>new R2MemoryService(restarted,restarted).WhyAsync("other",afternoon.AssertionId));
    }

    [Fact]
    public async Task Secret_like_memory_is_rejected_without_evidence_or_assertion_persistence()
    {
        using var database=new TemporaryDatabase();var store=database.CreateStore();await store.InitializeAsync();var now=DateTimeOffset.UtcNow;
        var owner=PrincipalRef.Create("https://issuer.example","tenant","subject","owner",now);await store.AddAsync(owner);
        var service=new R2MemoryService(store,store);

        await Assert.ThrowsAsync<ArgumentException>(()=>service.RememberAsync(
            owner.PrincipalId,"user","preference","Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123456789","m-secret",now));
        await Assert.ThrowsAsync<ArgumentException>(()=>service.RememberAsync(
            owner.PrincipalId,"user","preference","{\"access_token\":\"not-for-product-storage\"}","m-json-secret",now));

        Assert.Empty(await store.ListEvidenceAsync(owner.PrincipalId));
        Assert.Empty(await store.ListCurrentAsync(owner.PrincipalId));
    }
}