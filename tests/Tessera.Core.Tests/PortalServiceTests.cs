using Tessera.Core.Configuration;
using Tessera.Core.Policy;
using Tessera.Core.Portal;
using Tessera.Core.Recipes;
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

    // ── Delegations (ADR 0017) ────────────────────────────────────────────────

    private static PortalService DelegationPortal() =>
        new(
            new LoadedPolicy(
                Grants:
                [
                    new Grant("spiffe://tessera.local/chat", "health-portal", ["read:*"], OnBehalfOf: Admin, StepUpActions: ["write:*"]),
                    new Grant("spiffe://tessera.local/chat", "health-portal", ["read:appointments"], OnBehalfOf: Member),
                    new Grant("portal://tessera", "utility-co", ["read:*"]),
                ],
                Bindings: [],
                Recipes: [new Recipe("health-portal", Description: "Health Portal")]),
            new CredentialResolver([], new InMemoryCredentialStore()),
            [Admin]);

    [Fact]
    public void Delegations_filtered_by_principal_returns_only_that_persons_grants()
    {
        var mine = DelegationPortal().ListDelegations(Member);

        var grant = Assert.Single(mine);
        Assert.Equal(Member, grant.OnBehalfOf);
        Assert.Equal("health-portal", grant.Target);
        Assert.False(grant.IsAutomation);
    }

    [Fact]
    public void Delegations_resolve_recipe_display_name_and_step_up()
    {
        var mine = DelegationPortal().ListDelegations(Admin);

        var grant = Assert.Single(mine);
        Assert.Equal("Health Portal", grant.DisplayName);   // from the recipe description
        Assert.Contains("read:*", grant.Actions);
        Assert.Contains("write:*", grant.StepUpActions);
    }

    [Fact]
    public void Delegations_null_principal_returns_all_including_automation()
    {
        var all = DelegationPortal().ListDelegations(null);

        Assert.Equal(3, all.Count);
        Assert.Contains(all, d => d.IsAutomation && d.OnBehalfOf is null && d.Target == "utility-co");
        Assert.Contains(all, d => d.OnBehalfOf == Admin);
        Assert.Contains(all, d => d.OnBehalfOf == Member);
    }

    [Fact]
    public void Delegations_for_a_person_with_no_grants_is_empty()
    {
        Assert.Empty(DelegationPortal().ListDelegations("carol@example.com"));
    }

    [Fact]
    public void Delegations_surface_the_backing_credential_owner_including_a_shared_key()
    {
        // bob has a grant on a media target backed only by a shared service key
        // (no per-person binding) — the "who can act as me" view must still say so.
        var portal = new PortalService(
            new LoadedPolicy(
                Grants:
                [
                    new Grant("chat://librechat", "seerr", ["read:*", "use:request"], OnBehalfOf: Member),
                    new Grant("chat://librechat", "health-portal", ["read:*"], OnBehalfOf: Admin),
                ],
                Bindings:
                [
                    new TargetBinding("seerr", "seerr-key", Principal: null, Owner: CredentialOwner.Service),
                    new TargetBinding("health-portal", "hp-alice", Admin, CredentialOwner.User),
                ],
                Recipes: []),
            new CredentialResolver([], new InMemoryCredentialStore()),
            [Admin]);

        var bob = portal.ListDelegations(Member).Single();
        Assert.Equal("service", bob.Owner); // a shared household key stands in for bob

        var alice = portal.ListDelegations(Admin).Single();
        Assert.Equal("user", alice.Owner); // alice's own login
    }

    // ── Modules (ADR 0017) ────────────────────────────────────────────────────

    private static PortalService ModulePortal() =>
        new(
            new LoadedPolicy(
                Grants: [],
                Bindings:
                [
                    new TargetBinding("health-portal", "hp-alice", Admin),
                    new TargetBinding("health-portal", "hp-bob", Member),
                ],
                Recipes:
                [
                    new Recipe("health-portal", Description: "Health Portal"),
                    new Recipe(
                        "market",
                        Egress: EgressMode.Http,
                        UpstreamBaseUrl: "https://api.example.com/v1",
                        Actions: ["read:listings"],
                        Tools: [new RecipeTool("list", "GET", "/listings", "read:listings")],
                        Description: "Marketplace"),
                ]),
            new CredentialResolver([], new InMemoryCredentialStore()),
            [Admin]);

    [Fact]
    public void Modules_project_egress_posture_and_usage_counts()
    {
        var modules = ModulePortal().ListModules(egressGloballyEnabled: true);

        var hp = modules.Single(m => m.Target == "health-portal");
        Assert.Equal("none", hp.Egress);
        Assert.False(hp.EgressEnabled);          // status-only never egresses
        Assert.Null(hp.UpstreamHost);
        Assert.Equal(0, hp.ToolCount);
        Assert.Equal(2, hp.ConnectionCount);     // alice + bob

        var mk = modules.Single(m => m.Target == "market");
        Assert.Equal("http", mk.Egress);
        Assert.True(mk.EgressEnabled);           // http AND the global gate is on
        Assert.Equal("api.example.com", mk.UpstreamHost);
        Assert.Equal(1, mk.ToolCount);
        Assert.Equal(0, mk.ConnectionCount);
    }

    [Fact]
    public void Modules_are_not_egress_enabled_when_the_global_gate_is_off()
    {
        var mk = ModulePortal().ListModules(egressGloballyEnabled: false).Single(m => m.Target == "market");

        Assert.Equal("http", mk.Egress);         // the posture is still http …
        Assert.False(mk.EgressEnabled);          // … but it cannot egress right now
    }

    [Fact]
    public void Modules_surface_the_host_only_never_the_path()
    {
        var serialized = System.Text.Json.JsonSerializer.Serialize(ModulePortal().ListModules(egressGloballyEnabled: true));

        Assert.Contains("api.example.com", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("/v1", serialized, StringComparison.Ordinal);   // no path, no secret-bearing URL
    }

    // ── Schedule (ADR 0017) ────────────────────────────────────────────────────

    private static PortalService SchedulePortal() =>
        new(
            new LoadedPolicy(
                Grants: [],
                Bindings:
                [
                    new TargetBinding("health-portal", "hp-alice", Admin),
                    new TargetBinding("static-co", "sc-alice", Admin),
                ],
                Recipes:
                [
                    new Recipe("health-portal", Description: "Health Portal",
                        Rotation: new RecipeRotation("external", "a domain MCP keep-warm owns rotation")),
                    new Recipe("static-co", Description: "Static Co"),
                ]),
            new CredentialResolver([], new InMemoryCredentialStore()),
            [Admin]);

    [Fact]
    public void Schedule_reports_external_rotation_owner_when_declared()
    {
        var s = SchedulePortal().GetSchedule($"health-portal:{Admin}");

        Assert.NotNull(s);
        Assert.Equal("external", s!.RotationOwner);
        Assert.True(s.RefreshConfigured);
        Assert.Contains("keep-warm", s.Detail, StringComparison.Ordinal);
        Assert.Null(s.LastRotatedAt);    // Tessera does not track external rotation
        Assert.Null(s.NextRotationAt);
    }

    [Fact]
    public void Schedule_reports_none_for_a_static_session()
    {
        var s = SchedulePortal().GetSchedule($"static-co:{Admin}");

        Assert.NotNull(s);
        Assert.Equal("none", s!.RotationOwner);
        Assert.False(s.RefreshConfigured);
    }

    [Fact]
    public void Schedule_is_null_for_an_unknown_or_malformed_connection()
    {
        Assert.Null(SchedulePortal().GetSchedule("health-portal:nobody@example.com"));
        Assert.Null(SchedulePortal().GetSchedule("garbage-no-colon"));
    }
}
