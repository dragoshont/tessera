using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Tessera.Core.Stores;
using Tessera.Persistence.Sqlite;
using Tessera.Providers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tessera.Broker.Tests;

public sealed class R2ConnectedAccountServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tessera-r2-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Connect_persists_only_opaque_ref_and_revoke_empties_custody_first_class()
    {
        var product = new SqliteKernelStore(_path); await product.InitializeAsync();
        var principal = PrincipalRef.Create("https://issuer.example","tenant","subject","owner",DateTimeOffset.UtcNow);
        await product.AddAsync(principal);
        var custody = new InMemoryCredentialStore();
        var service = new R2ConnectedAccountService(product,custody);
        var account = await service.ConnectAsync(principal.PrincipalId,"a1","github","github","1.0.0","GitHub","{}",
            new CredentialBundle(AccessToken:"fine-grained-pat"),["issues:read"],[new("github","1.0.0","github.issues.list","1")]);
        Assert.StartsWith("r2/account/",account.CredentialRef);
        Assert.True((await custody.GetBundleAsync(account.CredentialRef)).HasAccessToken);
        var revoked = await service.RevokeAsync(principal.PrincipalId,"a1",1);
        Assert.Equal(AccountLifecycle.Revoked,revoked.Lifecycle);
        Assert.True((await custody.GetBundleAsync(account.CredentialRef)).IsEmpty);
    }

    [Fact]
    public async Task Revoke_rejects_tampered_credential_ref_before_state_or_custody_change()
    {
        var product = new SqliteKernelStore(_path); await product.InitializeAsync();
        var principal = PrincipalRef.Create("https://issuer.example","tenant","subject","owner",DateTimeOffset.UtcNow);
        await product.AddAsync(principal);
        var now = DateTimeOffset.UtcNow;
        await product.AddConnectedAccountAsync(new(principal.PrincipalId,"a1","github","github","1.0.0","GitHub",null,
            AccountLifecycle.Connected,"r2/account/tampered/a1",AccountHealth.Healthy,null,"{}",[],[],now,now,1));
        var custody = new RecordingCredentialStore();
        var service = new R2ConnectedAccountService(product,custody);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RevokeAsync(principal.PrincipalId,"a1",1));

        await using var connection = new SqliteConnection($"Data Source={_path}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT lifecycle FROM connected_accounts WHERE owner_principal_id=$owner AND account_id='a1';";
        command.Parameters.AddWithValue("$owner",principal.PrincipalId);
        Assert.Equal("CONNECTED",await command.ExecuteScalarAsync());
        Assert.Equal(0,custody.WriteCount);
    }

    [Fact]
    public async Task Failed_compensation_is_retried_from_durable_cleanup_intent()
    {
        var product = new SqliteKernelStore(_path); await product.InitializeAsync();
        var principal = PrincipalRef.Create("https://issuer.example","tenant","subject","owner",DateTimeOffset.UtcNow);
        await product.AddAsync(principal);
        var now=DateTimeOffset.UtcNow;
        await product.AddConnectedAccountAsync(new(principal.PrincipalId,"duplicate","github","github","1.0.0","Existing",null,AccountLifecycle.Connecting,R2ConnectedAccountService.CredentialRef(principal.PrincipalId,"duplicate"),AccountHealth.Unknown,null,"{}",[],[],now,now,1));
        var custody=new FailingCleanupStore();var service=new R2ConnectedAccountService(product,custody);
        await Assert.ThrowsAsync<R2AccountStorageException>(()=>service.ConnectAsync(principal.PrincipalId,"duplicate","github","github","1.0.0","Duplicate","{}",new CredentialBundle(AccessToken:"pat"),[],[]));
        Assert.Single(await product.ListPendingOrphanCleanupAsync());
        custody.FailCleanup=false;
        var scheduler=new R2SchedulerService(product,custody,new NoopTransport(),NullLogger<R2SchedulerService>.Instance);
        await scheduler.ProcessCleanupAsync(CancellationToken.None);
        Assert.Empty(await product.ListPendingOrphanCleanupAsync());
        Assert.True((await custody.GetBundleAsync(R2ConnectedAccountService.CredentialRef(principal.PrincipalId,"duplicate"))).IsEmpty);
    }

    [Fact]
    public async Task Revoke_commits_cleanup_intent_atomically_when_custody_delete_fails()
    {
        var product=new SqliteKernelStore(_path);await product.InitializeAsync();var principal=PrincipalRef.Create("https://issuer.example","tenant","subject","owner",DateTimeOffset.UtcNow);await product.AddAsync(principal);var now=DateTimeOffset.UtcNow;var credentialRef=R2ConnectedAccountService.CredentialRef(principal.PrincipalId,"a1");await product.AddConnectedAccountAsync(new(principal.PrincipalId,"a1","github","github","1.0.0","GitHub",null,AccountLifecycle.Connected,credentialRef,AccountHealth.Healthy,null,"{}",[],[],now,now,1));var custody=new FailingCleanupStore();var service=new R2ConnectedAccountService(product,custody);

        var revoked=await service.RevokeAsync(principal.PrincipalId,"a1",1);

        Assert.Equal(AccountLifecycle.Revoked,revoked.Lifecycle);Assert.Single(await product.ListPendingOrphanCleanupAsync());custody.FailCleanup=false;await new R2SchedulerService(product,custody,new NoopTransport(),NullLogger<R2SchedulerService>.Instance).ProcessCleanupAsync(CancellationToken.None);Assert.Empty(await product.ListPendingOrphanCleanupAsync());
    }

    [Fact]
    public async Task Connect_rejects_secret_like_display_name_before_custody_or_sqlite()
    {var product=new SqliteKernelStore(_path);await product.InitializeAsync();var principal=PrincipalRef.Create("https://issuer.example","tenant","subject","owner",DateTimeOffset.UtcNow);await product.AddAsync(principal);var custody=new RecordingCredentialStore();var service=new R2ConnectedAccountService(product,custody);await Assert.ThrowsAsync<ArgumentException>(()=>service.ConnectAsync(principal.PrincipalId,"unsafe","github","github","1.0.0","Authorization: Bearer abcdefghijklmnopqrstuvwxyz","{}",new CredentialBundle(AccessToken:"pat"),[],[]));Assert.Equal(0,custody.WriteCount);Assert.Null(await product.GetConnectedAccountAsync(principal.PrincipalId,"unsafe"));}

    private sealed class FailingCleanupStore : ICredentialStore,ICredentialWriter
    {
        private CredentialBundle _bundle=CredentialBundle.Empty;
        public bool FailCleanup{get;set;}=true;
        public string Kind=>"test";
        public Task<CredentialBundle> GetBundleAsync(string name,CancellationToken cancellationToken=default)=>Task.FromResult(_bundle);
        public Task PutBundleAsync(string name,CredentialBundle bundle,CancellationToken cancellationToken=default)
        {if(bundle.IsEmpty&&FailCleanup)throw new StoreException("cleanup failed");_bundle=bundle;return Task.CompletedTask;}
    }

    private sealed class NoopTransport : IHttpTransport
    {public Task<TransportResponse> SendAsync(string method,string url,IReadOnlyDictionary<string,string> headers,string? body,CancellationToken cancellationToken=default)=>Task.FromResult(new TransportResponse(503,new Dictionary<string,string>(),"{}"));}

    private sealed class RecordingCredentialStore : ICredentialWriter
    {
        public int WriteCount { get; private set; }
        public Task PutBundleAsync(string name,CredentialBundle bundle,CancellationToken cancellationToken=default)
        {WriteCount++;return Task.CompletedTask;}
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(_path + suffix)) File.Delete(_path + suffix);
    }
}