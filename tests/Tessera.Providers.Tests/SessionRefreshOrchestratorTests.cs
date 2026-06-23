using Tessera.Core.Configuration;
using Tessera.Core.Health;
using Tessera.Core.Policy;
using Tessera.Core.Recipes;
using Tessera.Core.Resolution;
using Tessera.Core.Stores;
using Xunit;

namespace Tessera.Providers.Tests;

public sealed class SessionRefreshOrchestratorTests
{
    private static Recipe OwnedPortal() => new(
        "portal",
        Egress: EgressMode.Http,
        UpstreamBaseUrl: "https://api.example.com/v1",
        Injection: InjectionKind.Cookies,
        Rotation: new RecipeRotation("tessera"),
        Refresh: new RefreshSpec("refresh"));

    private static CredentialBundle Live() => new(
        AccessToken: "OLD_AT",
        RefreshToken: "OLD_RT",
        Cookies: new Dictionary<string, string> { ["session"] = "OLD" });

    private static LoadedPolicy Policy(IReadOnlyList<TargetBinding> bindings, params Recipe[] recipes) =>
        new([], bindings, recipes);

    private static readonly Tessera.Core.Egress.SsrfGuard Guard = new(["api.example.com"]);

    [Fact]
    public async Task Rotates_owned_bindings_and_skips_absent_ones()
    {
        var bindings = new[]
        {
            new TargetBinding("portal", "portal-alice", "alice@example.com", CredentialOwner.User),
            new TargetBinding("portal", "portal-bob", "bob@example.com", CredentialOwner.User), // no bundle → skipped
        };
        var store = new InMemoryCredentialStore();
        store.Put("portal-alice", Live()); // only alice is seeded

        var writer = new CapturingWriter();
        var transport = new FakeTransport(200, "{\"access_token\":\"NEW_AT\",\"refresh_token\":\"NEW_RT\"}");
        var orchestrator = new SessionRefreshOrchestrator(
            Policy(bindings, OwnedPortal()), store, new SessionRefresher(transport, writer, Guard));

        var summary = await orchestrator.RunPassAsync();

        Assert.Equal(2, summary.Considered);
        Assert.Equal(1, summary.Rotated);
        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, summary.Errors);
        Assert.Equal("portal-alice", writer.LastName); // only the seeded one was written back
        Assert.Equal("NEW_AT", writer.LastBundle!.AccessToken);
    }

    [Fact]
    public async Task Ignores_a_recipe_owned_by_an_external_component()
    {
        // rotation.owner = external → Tessera must NOT touch it (it would corrupt a
        // single-use session another component owns).
        var external = OwnedPortal() with { Rotation = new RecipeRotation("external") };
        var bindings = new[] { new TargetBinding("portal", "portal-alice", "alice@example.com") };
        var store = new InMemoryCredentialStore();
        store.Put("portal-alice", Live());
        var writer = new CapturingWriter();
        var orchestrator = new SessionRefreshOrchestrator(
            Policy(bindings, external), store, new SessionRefresher(new FakeTransport(), writer, Guard));

        var summary = await orchestrator.RunPassAsync();

        Assert.Equal(0, summary.Considered);
        Assert.False(orchestrator.HasOwnedRotation);
        Assert.Null(writer.LastBundle); // never wrote
    }

    [Fact]
    public async Task Ignores_an_owned_recipe_with_no_refresh_spec()
    {
        var noRefresh = new Recipe("portal", Rotation: new RecipeRotation("tessera")); // Refresh = null
        var bindings = new[] { new TargetBinding("portal", "portal-alice", "alice@example.com") };
        var store = new InMemoryCredentialStore();
        store.Put("portal-alice", Live());
        var orchestrator = new SessionRefreshOrchestrator(
            Policy(bindings, noRefresh), store, new SessionRefresher(new FakeTransport(), new CapturingWriter(), Guard));

        Assert.False(orchestrator.HasOwnedRotation);
        Assert.Equal(0, (await orchestrator.RunPassAsync()).Considered);
    }

    [Fact]
    public async Task A_dead_refresh_token_is_tallied_not_relogged_in()
    {
        var bindings = new[] { new TargetBinding("portal", "portal-alice", "alice@example.com") };
        var store = new InMemoryCredentialStore();
        store.Put("portal-alice", Live());
        var writer = new CapturingWriter();
        var orchestrator = new SessionRefreshOrchestrator(
            Policy(bindings, OwnedPortal()), store, new SessionRefresher(new FakeTransport(401, "unauthorized"), writer, Guard));

        var summary = await orchestrator.RunPassAsync();

        Assert.Equal(1, summary.Dead);
        Assert.Equal(0, summary.Rotated);
        Assert.Null(writer.LastBundle); // never wrote, never drove a login
    }

    [Fact]
    public async Task Harvests_the_rotation_plane_verdict_into_the_liveness_store()
    {
        // SDD-01 P4 (judge C2): the keep-warm pass is the most incident-relevant liveness
        // signal for an RM-style session, so a rotation proves alive and a dead refresh
        // token proves dead in the SAME store the portal reads.
        var bindings = new[] { new TargetBinding("portal", "portal-alice", "alice@example.com", CredentialOwner.User) };
        const string key = "portal:alice@example.com";

        // A successful rotation ⇒ alive.
        var aliveStore = new InMemoryCredentialStore();
        aliveStore.Put("portal-alice", Live());
        var health = new InMemoryConnectionHealthStore();
        var rotated = new SessionRefreshOrchestrator(
            Policy(bindings, OwnedPortal()), aliveStore,
            new SessionRefresher(new FakeTransport(200, "{\"access_token\":\"NEW_AT\"}"), new CapturingWriter(), Guard),
            health);
        await rotated.RunPassAsync();
        var aliveRecord = await health.GetAsync(key);
        Assert.True(aliveRecord!.VerifiedAlive);
        Assert.NotNull(aliveRecord.LastVerifiedAt);

        // A dead refresh token ⇒ dead, in the same store.
        var deadStore = new InMemoryCredentialStore();
        deadStore.Put("portal-alice", Live());
        var deadHealth = new InMemoryConnectionHealthStore();
        var dead = new SessionRefreshOrchestrator(
            Policy(bindings, OwnedPortal()), deadStore,
            new SessionRefresher(new FakeTransport(401, "unauthorized"), new CapturingWriter(), Guard),
            deadHealth);
        await dead.RunPassAsync();
        var deadRecord = await deadHealth.GetAsync(key);
        Assert.False(deadRecord!.VerifiedAlive);
    }

    [Fact]
    public void HasOwnedRotation_is_true_only_when_a_recipe_is_owned_and_refresh_declaring()
    {
        var owned = new SessionRefreshOrchestrator(
            Policy([], OwnedPortal()), new InMemoryCredentialStore(), new SessionRefresher(new FakeTransport(), new CapturingWriter(), Guard));
        Assert.True(owned.HasOwnedRotation);

        var none = new SessionRefreshOrchestrator(
            Policy([], new Recipe("portal")), new InMemoryCredentialStore(), new SessionRefresher(new FakeTransport(), new CapturingWriter(), Guard));
        Assert.False(none.HasOwnedRotation);
    }

    [Fact]
    public async Task Reads_the_live_policy_each_pass_so_a_later_binding_is_picked_up()
    {
        // Start with no bindings; add one between passes via the live policy source.
        var store = new InMemoryCredentialStore();
        store.Put("portal-alice", Live());
        var writer = new CapturingWriter();
        var transport = new FakeTransport(200, "{\"access_token\":\"NEW_AT\"}");

        var bindings = new List<TargetBinding>();
        var current = Policy(bindings, OwnedPortal());
        var orchestrator = new SessionRefreshOrchestrator(
            () => current, store, new SessionRefresher(transport, writer, Guard));

        var first = await orchestrator.RunPassAsync();
        Assert.Equal(0, first.Considered); // no binding yet

        // A portal add-connection would swap the policy snapshot — simulate it.
        current = Policy([new TargetBinding("portal", "portal-alice", "alice@example.com", CredentialOwner.User)], OwnedPortal());

        var second = await orchestrator.RunPassAsync();
        Assert.Equal(1, second.Considered); // picked up without a restart
        Assert.Equal(1, second.Rotated);
    }

    private sealed class CapturingWriter : ICredentialWriter
    {
        public string? LastName { get; private set; }
        public CredentialBundle? LastBundle { get; private set; }

        public Task PutBundleAsync(string name, CredentialBundle bundle, CancellationToken cancellationToken = default)
        {
            LastName = name;
            LastBundle = bundle;
            return Task.CompletedTask;
        }
    }
}
