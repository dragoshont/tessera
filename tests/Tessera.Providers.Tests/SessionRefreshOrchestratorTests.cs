using Tessera.Core.Configuration;
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
            Policy(bindings, OwnedPortal()), store, new SessionRefresher(transport, writer));

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
            Policy(bindings, external), store, new SessionRefresher(new FakeTransport(), writer));

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
            Policy(bindings, noRefresh), store, new SessionRefresher(new FakeTransport(), new CapturingWriter()));

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
            Policy(bindings, OwnedPortal()), store, new SessionRefresher(new FakeTransport(401, "unauthorized"), writer));

        var summary = await orchestrator.RunPassAsync();

        Assert.Equal(1, summary.Dead);
        Assert.Equal(0, summary.Rotated);
        Assert.Null(writer.LastBundle); // never wrote, never drove a login
    }

    [Fact]
    public void HasOwnedRotation_is_true_only_when_a_recipe_is_owned_and_refresh_declaring()
    {
        var owned = new SessionRefreshOrchestrator(
            Policy([], OwnedPortal()), new InMemoryCredentialStore(), new SessionRefresher(new FakeTransport(), new CapturingWriter()));
        Assert.True(owned.HasOwnedRotation);

        var none = new SessionRefreshOrchestrator(
            Policy([], new Recipe("portal")), new InMemoryCredentialStore(), new SessionRefresher(new FakeTransport(), new CapturingWriter()));
        Assert.False(none.HasOwnedRotation);
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
