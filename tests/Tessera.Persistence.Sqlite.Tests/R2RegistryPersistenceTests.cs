using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Xunit;

namespace Tessera.Persistence.Sqlite.Tests;

public sealed class R2RegistryPersistenceTests
{
    [Fact]
    public async Task Accounts_are_owner_scoped_restart_durable_and_allow_same_provider()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore(); await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var owner = PrincipalRef.Create("https://issuer.example", "tenant", "subject", "owner", now);
        var other = PrincipalRef.Create("https://issuer.example", "tenant", "other", "other", now);
        await store.AddAsync(owner); await store.AddAsync(other);
        await store.AddConnectedAccountAsync(Account(owner.PrincipalId, "a1", now));
        await store.AddConnectedAccountAsync(Account(owner.PrincipalId, "a2", now));

        var restarted = database.CreateStore(); await restarted.InitializeAsync();
        var accounts = await restarted.ListConnectedAccountsAsync(owner.PrincipalId);
        Assert.Equal(2, accounts.Count);
        Assert.All(accounts, item => Assert.Equal("github", item.ProviderId));
        Assert.Empty(await restarted.ListConnectedAccountsAsync(other.PrincipalId));
        Assert.Equal(ConnectedAccountCredentialRef.Create(owner.PrincipalId, "a1"), (await restarted.GetConnectedAccountAsync(owner.PrincipalId, "a1"))!.CredentialRef);
    }

    [Fact]
    public async Task Account_state_update_rejects_stale_version()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore(); await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var owner = PrincipalRef.Create("https://issuer.example", "tenant", "subject", "owner", now); await store.AddAsync(owner);
        await store.AddConnectedAccountAsync(Account(owner.PrincipalId, "a1", now));

        var updated = await store.SetConnectedAccountStateAsync(owner.PrincipalId, "a1", 1, AccountLifecycle.Connected, AccountHealth.Healthy);
        Assert.Equal(2, updated.Version);
        await Assert.ThrowsAsync<ProductConcurrencyException>(() => store.SetConnectedAccountStateAsync(
            owner.PrincipalId, "a1", 1, AccountLifecycle.Disabled, AccountHealth.Unknown));
    }

    [Fact]
    public async Task Plugin_and_model_profile_foreign_keys_survive_restart()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore(); await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var owner = PrincipalRef.Create("https://issuer.example", "tenant", "subject", "owner", now); await store.AddAsync(owner);
        await store.AddPluginInstallationAsync(new(owner.PrincipalId,"github","1.0.0","GitHub","Tessera","abc",GitHubManifest,"{}",true,now,now,1));
        await store.AddConnectedAccountAsync(Account(owner.PrincipalId, "a1", now));
        await store.AddModelProfileAsync(new(owner.PrincipalId,"model-1","a1","openai-compatible-remote","https://model.example/v1","alpha",8192,true,true,true,now,now,1));

        var restarted = database.CreateStore(); await restarted.InitializeAsync();
        Assert.NotNull(await restarted.GetConnectedAccountAsync(owner.PrincipalId, "a1"));
    }

    [Fact]
    public async Task Removed_plugin_stays_absent_and_preserves_historical_evidence()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore(); await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var owner = PrincipalRef.Create("https://issuer.example", "tenant", "subject", "owner", now); await store.AddAsync(owner);
        await store.AddPluginInstallationAsync(new(owner.PrincipalId,"github","1.0.0","GitHub","Tessera","abc",GitHubManifest,"{}",true,now,now,1));
        var evidence = EvidenceRecord.Create(
            "historical-github",
            owner.PrincipalId,
            "capability.result",
            "old-call",
            "tessera://capability/github.issues.list/old-call",
            now,
            now,
            "sha256",
            1,
            new string('a', 64),
            RetentionState.Active,
            SensitivityClass.Internal,
            ProducerRef.Create("plugin:github", "1.0.0"),
            1);
        await ((IEvidenceRepository)store).AddAsync(owner.PrincipalId, evidence);

        Assert.Null(await store.RemovePluginAsync(owner.PrincipalId, "github", "1.0.0", 1));
        Assert.Empty(await store.ListPluginInstallationsAsync(owner.PrincipalId));
        Assert.Null(await store.GetPluginInstallationAsync(owner.PrincipalId, "github", "1.0.0"));
        Assert.False(await store.SetPluginEnabledAsync(owner.PrincipalId, "github", "1.0.0", 2, true));

        var restarted = database.CreateStore(); await restarted.InitializeAsync();
        Assert.Empty(await restarted.ListPluginInstallationsAsync(owner.PrincipalId));
        Assert.NotNull(await ((IEvidenceRepository)restarted).GetAsync(owner.PrincipalId, evidence.EvidenceId));
    }

    [Fact]
    public async Task Availability_fails_closed_when_account_is_disabled()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore(); await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var owner = PrincipalRef.Create("https://issuer.example", "tenant", "subject", "owner", now); await store.AddAsync(owner);
        await store.AddPluginInstallationAsync(new(owner.PrincipalId,"github","1.0.0","GitHub","Tessera","abc",GitHubManifest,"{}",true,now,now,1));
        await store.AddConnectedAccountAsync(Account(owner.PrincipalId, "a1", now));
        await store.SetConnectedAccountStateAsync(owner.PrincipalId,"a1",1,AccountLifecycle.Connected,AccountHealth.Healthy);
        using var input = System.Text.Json.JsonDocument.Parse("{}");
        var request = new ExecutionRequest(owner.PrincipalId,"e1","github.issues.list","1","github","1.0.0","a1","owner/repo","hash",input.RootElement.Clone(),"key");
        Assert.True((await store.CheckAsync(request)).Available);
        await store.SetConnectedAccountStateAsync(owner.PrincipalId,"a1",2,AccountLifecycle.Disabled,AccountHealth.Unknown);
        Assert.Equal("account_unavailable", (await store.CheckAsync(request)).BlockedCode);
    }

    [Fact]
    public async Task Tampered_credential_reference_fails_at_repository_boundary()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore(); await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow;
        var owner = PrincipalRef.Create("https://issuer.example", "tenant", "subject", "owner", now); await store.AddAsync(owner);
        await store.AddConnectedAccountAsync(Account(owner.PrincipalId, "a1", now) with { CredentialRef = "r2/account/tampered/a1" });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.GetConnectedAccountAsync(owner.PrincipalId, "a1"));
    }

    private static ConnectedAccount Account(string owner, string id, DateTimeOffset now) => new(
        owner,id,"github","github","1.0.0",id,null,AccountLifecycle.Connecting,ConnectedAccountCredentialRef.Create(owner,id),AccountHealth.Unknown,null,"{}",
        ["issues:read"],[new("github","1.0.0","github.issues.list","1")],now,now,1);

    private const string GitHubManifest = """{"Id":"github","Version":"1.0.0","Name":"GitHub","Publisher":"Tessera","MinimumTesseraVersion":"2.0.0","Capabilities":[{"Id":"github.issues.list","Version":"1","Description":"List issues","ExecutorKind":"github-rest","AccountRequired":true,"RequiredPermissions":["issues:read"],"SideEffectClass":"ReadOnly","TimeoutMilliseconds":30000,"MaxResultBytes":32768}]}""";
}