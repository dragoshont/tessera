using Tessera.Core.Configuration;
using Tessera.Core.Portal;
using Tessera.Core.Resolution;
using Tessera.Core.Stores;
using Xunit;

namespace Tessera.Core.Tests;

/// <summary>
/// The portal read-model that touches the store: connection health (presence flags
/// + status) and the per-person attention rollup. Uses generic identities — alice
/// is the operator, bob and carol members — without naming a real person. No secret
/// value is ever part of the projection.
/// </summary>
public sealed class PortalServiceTests
{
    private const string Admin = "alice@example.com";
    private const string Member = "bob@example.com";

    private static PortalService Build(InMemoryCredentialStore store, LoadedPolicy policy, params string[] admins)
    {
        var resolver = new CredentialResolver(policy.Bindings, store);
        return new PortalService(policy, resolver, admins);
    }

    private static LoadedPolicy Policy(params TargetBinding[] bindings) =>
        new(Grants: [], Bindings: bindings, Recipes: []);

    [Fact]
    public void IsAdmin_uses_the_allow_list_case_insensitively()
    {
        var portal = Build(new InMemoryCredentialStore(), Policy(), "ALICE@example.com");

        Assert.True(portal.IsAdmin(Admin));
        Assert.False(portal.IsAdmin(Member));
        Assert.False(portal.IsAdmin(null));
    }

    [Fact]
    public async Task A_present_bundle_is_live_with_presence_flags_but_no_value()
    {
        var store = new InMemoryCredentialStore();
        store.Put("health-portal-alice", new CredentialBundle(
            RefreshToken: "secret-refresh", Cookies: new Dictionary<string, string> { ["Session"] = "secret-cookie" }));
        var policy = Policy(new TargetBinding("health-portal", "health-portal-alice", Admin));

        var portal = Build(store, policy, Admin);
        var connection = Assert.Single(await portal.ListConnectionsAsync(Admin));

        Assert.Equal("live", connection.Status);
        Assert.True(connection.HasRefreshToken);
        Assert.True(connection.HasCookies);
        Assert.False(connection.HasAccessToken);
        Assert.True(connection.ExpiryIsEstimated);   // honest: no readable TTL
        Assert.Null(connection.ExpiresAt);

        // The secret-free contract: no field anywhere carries the actual values.
        var serialized = System.Text.Json.JsonSerializer.Serialize(connection);
        Assert.DoesNotContain("secret-refresh", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-cookie", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_bundle_is_absent()
    {
        // No bundle put → the store returns Empty → absent (needs a seed).
        var policy = Policy(new TargetBinding("marketplace", "marketplace-bob", Member));
        var portal = Build(new InMemoryCredentialStore(), policy, Admin);

        var connection = Assert.Single(await portal.ListConnectionsAsync(Member));
        Assert.Equal("absent", connection.Status);
        Assert.False(connection.HasCookies);
    }

    [Fact]
    public async Task Connections_are_scoped_to_the_requested_principal()
    {
        var store = new InMemoryCredentialStore();
        store.Put("health-portal-alice", new CredentialBundle(RefreshToken: "a"));
        store.Put("health-portal-bob", new CredentialBundle(RefreshToken: "b"));
        var policy = Policy(
            new TargetBinding("health-portal", "health-portal-alice", Admin),
            new TargetBinding("health-portal", "health-portal-bob", Member));

        var portal = Build(store, policy, Admin);

        var aliceConns = await portal.ListConnectionsAsync(Admin);
        Assert.Single(aliceConns);
        Assert.Equal(Admin, aliceConns[0].OwnerPrincipal);

        var bobConns = await portal.ListConnectionsAsync(Member);
        Assert.Single(bobConns);
        Assert.Equal(Member, bobConns[0].OwnerPrincipal);
    }

    [Fact]
    public async Task People_rollup_counts_only_unhealthy_connections_as_needing_attention()
    {
        var store = new InMemoryCredentialStore();
        // alice: one live (present) + one absent (empty) → needsAttention = 1.
        store.Put("health-portal-alice", new CredentialBundle(RefreshToken: "x"));
        var policy = Policy(
            new TargetBinding("health-portal", "health-portal-alice", Admin),
            new TargetBinding("utility-co", "utility-co-alice", Admin));

        var portal = Build(store, policy, Admin);
        var person = Assert.Single(await portal.ListPeopleAsync());

        Assert.Equal(Admin, person.Principal);
        Assert.Equal(PortalRole.Admin, person.Role);
        Assert.Equal(2, person.ConnectionCount);
        Assert.Equal(1, person.NeedsAttentionCount);
    }

    [Fact]
    public async Task Unhealthy_connections_sort_first()
    {
        var store = new InMemoryCredentialStore();
        store.Put("bbb-alice", new CredentialBundle(RefreshToken: "x")); // live
        // aaa has no bundle → absent → must sort before the live bbb.
        var policy = Policy(
            new TargetBinding("bbb", "bbb-alice", Admin),
            new TargetBinding("aaa", "aaa-alice", Admin));

        var portal = Build(store, policy, Admin);
        var conns = await portal.ListConnectionsAsync(Admin);

        Assert.Equal("aaa", conns[0].Provider);   // absent first
        Assert.Equal("bbb", conns[1].Provider);
    }

    [Fact]
    public async Task Disabled_live_view_provider_fails_closed()
    {
        var result = await DisabledLiveViewProvider.Instance.RequestAsync("health-portal:alice@example.com", Admin);

        Assert.False(result.Issued);
        Assert.Null(result.Handle);
        Assert.Contains("not configured", result.Reason, StringComparison.Ordinal);
    }
}
