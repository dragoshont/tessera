using Tessera.Core.Configuration;
using Tessera.Core.Health;
using Tessera.Core.Policy;
using Tessera.Core.Portal;
using Tessera.Core.Resolution;
using Tessera.Core.Stores;
using Xunit;

namespace Tessera.Core.Tests;

/// <summary>
/// SDD-01 P4 — the use-based liveness verdict engine. Presence still never earns green
/// (ADR 0025); only a real call that confirmed the session alive does, and only while
/// that confirmation is fresh. The store is non-secret metadata, fail-closed on fault.
/// </summary>
public sealed class ConnectionHealthTests
{
    private const string Admin = "alice@example.com";
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);

    // --- The pure verdict function: every branch, including the decay and fail-closed paths. ---

    [Fact]
    public void Resolve_never_observed_is_unverified()
    {
        var (alive, at) = ConnectionHealthVerdict.Resolve(null, DateTimeOffset.UtcNow, MaxAge);
        Assert.Null(alive);   // ⇒ MapStatus(Present, null) = "unverified"
        Assert.Null(at);
    }

    [Fact]
    public void Resolve_unauthorized_is_dead_and_keeps_last_alive()
    {
        var now = DateTimeOffset.UtcNow;
        var lastAlive = now - TimeSpan.FromMinutes(5);
        var record = new ConnectionHealthRecord(VerifiedAlive: false, LastVerifiedAt: lastAlive, ConsecutiveFailures: 2);

        var (alive, at) = ConnectionHealthVerdict.Resolve(record, now, MaxAge);

        Assert.False(alive);             // ⇒ "dead"
        Assert.Equal(lastAlive, at);     // still shows when it was last alive
    }

    [Fact]
    public void Resolve_fresh_confirmation_is_live()
    {
        var now = DateTimeOffset.UtcNow;
        var lastAlive = now - TimeSpan.FromMinutes(30); // within the 1h bound
        var record = new ConnectionHealthRecord(VerifiedAlive: true, LastVerifiedAt: lastAlive);

        var (alive, at) = ConnectionHealthVerdict.Resolve(record, now, MaxAge);

        Assert.True(alive);              // ⇒ "live"
        Assert.Equal(lastAlive, at);
    }

    [Fact]
    public void Resolve_stale_confirmation_decays_to_unverified_never_green()
    {
        var now = DateTimeOffset.UtcNow;
        var lastAlive = now - TimeSpan.FromHours(3); // older than the 1h bound
        var record = new ConnectionHealthRecord(VerifiedAlive: true, LastVerifiedAt: lastAlive);

        var (alive, at) = ConnectionHealthVerdict.Resolve(record, now, MaxAge);

        Assert.Null(alive);              // decayed ⇒ "unverified", NOT a stale green
        Assert.Equal(lastAlive, at);     // but honestly shows the old confirmation time
    }

    [Fact]
    public void Resolve_alive_without_a_timestamp_is_not_green()
    {
        // Degenerate: a "true" verdict with no stamp can never be proven fresh.
        var record = new ConnectionHealthRecord(VerifiedAlive: true, LastVerifiedAt: null);
        var (alive, _) = ConnectionHealthVerdict.Resolve(record, DateTimeOffset.UtcNow, MaxAge);
        Assert.Null(alive);
    }

    // --- The in-memory store folds outcomes correctly. ---

    [Fact]
    public async Task Store_folds_alive_then_dead_then_alive()
    {
        var store = new InMemoryConnectionHealthStore();
        var t0 = DateTimeOffset.UtcNow;
        const string key = "health-portal:alice@example.com";

        await store.RecordOutcomeAsync(key, alive: true, t0);
        var afterAlive = await store.GetAsync(key);
        Assert.True(afterAlive!.VerifiedAlive);
        Assert.Equal(t0, afterAlive.LastVerifiedAt);
        Assert.Equal(0, afterAlive.ConsecutiveFailures);

        await store.RecordOutcomeAsync(key, alive: false, t0 + TimeSpan.FromMinutes(1));
        var afterDead = await store.GetAsync(key);
        Assert.False(afterDead!.VerifiedAlive);
        Assert.Equal(t0, afterDead.LastVerifiedAt);          // preserved across a failure
        Assert.Equal(1, afterDead.ConsecutiveFailures);

        var t2 = t0 + TimeSpan.FromMinutes(2);
        await store.RecordOutcomeAsync(key, alive: true, t2);
        var afterRecover = await store.GetAsync(key);
        Assert.True(afterRecover!.VerifiedAlive);
        Assert.Equal(t2, afterRecover.LastVerifiedAt);        // advanced
        Assert.Equal(0, afterRecover.ConsecutiveFailures);    // reset
    }

    [Fact]
    public async Task Store_returns_null_for_an_unseen_key()
        => Assert.Null(await new InMemoryConnectionHealthStore().GetAsync("nothing:here"));

    // --- SDD-02: a death is surfaced as a degradation (no more silent failure). ---

    [Fact]
    public async Task Store_surfaces_a_live_to_dead_degradation_once()
    {
        var store = new InMemoryConnectionHealthStore();
        const string key = "health-portal:alice@example.com";
        var t0 = DateTimeOffset.UtcNow;

        await store.RecordOutcomeAsync(key, alive: true, t0);                       // live
        Assert.Empty(store.RecentDegradations());                                   // healthy ⇒ nothing to report

        await store.RecordOutcomeAsync(key, alive: false, t0 + TimeSpan.FromMinutes(1)); // live → dead
        var first = Assert.Single(store.RecentDegradations());
        Assert.Equal(key, first.ConnectionKey);
        Assert.Equal("health-portal", first.Provider);
        Assert.Equal("alice@example.com", first.Principal);
        Assert.Equal("live", first.From);
        Assert.Equal("dead", first.To);
        Assert.Contains("portal", first.Remediation, StringComparison.OrdinalIgnoreCase); // actionable

        await store.RecordOutcomeAsync(key, alive: false, t0 + TimeSpan.FromMinutes(2)); // dead → dead
        Assert.Single(store.RecentDegradations());                                  // same outage, not re-reported
    }

    [Fact]
    public async Task Store_surfaces_an_unverified_to_dead_degradation()
    {
        var store = new InMemoryConnectionHealthStore();
        await store.RecordOutcomeAsync("portal:bob@example.com", alive: false, DateTimeOffset.UtcNow);

        var evt = Assert.Single(store.RecentDegradations());
        Assert.Equal("unverified", evt.From);   // never confirmed, then rejected
        Assert.Equal("dead", evt.To);
    }

    [Fact]
    public async Task Portal_surfaces_recent_degradations()
    {
        var (portal, health) = BuildPortal();
        await health.RecordOutcomeAsync("health-portal:alice@example.com", alive: true, DateTimeOffset.UtcNow);
        await health.RecordOutcomeAsync("health-portal:alice@example.com", alive: false, DateTimeOffset.UtcNow);

        var degradations = portal.RecentDegradations();
        var evt = Assert.Single(degradations);
        Assert.Equal("dead", evt.To);
    }

    [Fact]
    public void Portal_with_no_store_has_no_degradations()
    {
        var store = new InMemoryCredentialStore();
        var policy = new LoadedPolicy(Grants: [], Recipes: [], Bindings: []);
        var resolver = new CredentialResolver(policy.Bindings, store);
        var portal = new PortalService(policy, resolver, [Admin]); // no health store wired
        Assert.Empty(portal.RecentDegradations());
    }

    // --- The portal projects the earned verdict end-to-end. ---

    private static (PortalService portal, InMemoryConnectionHealthStore health) BuildPortal(IConnectionHealthStore? health = null)
    {
        var store = new InMemoryCredentialStore();
        store.Put("health-portal-alice", new CredentialBundle(RefreshToken: "secret-refresh"));
        var policy = new LoadedPolicy(
            Grants: [], Recipes: [],
            Bindings: [new TargetBinding("health-portal", "health-portal-alice", Admin)]);
        var resolver = new CredentialResolver(policy.Bindings, store);
        var metadata = (health as InMemoryConnectionHealthStore) ?? new InMemoryConnectionHealthStore();
        var portal = new PortalService(policy, resolver, [Admin], persist: null, health: health ?? metadata, freshnessMaxAge: MaxAge);
        return (portal, metadata);
    }

    [Fact]
    public async Task Portal_shows_live_only_after_a_fresh_confirmation()
    {
        var (portal, health) = BuildPortal();
        await health.RecordOutcomeAsync("health-portal:alice@example.com", alive: true, DateTimeOffset.UtcNow);

        var connection = Assert.Single(await portal.ListConnectionsAsync(Admin));

        Assert.Equal("live", connection.Status);          // earned, not assumed
        Assert.NotNull(connection.LastVerifiedAt);        // provenance surfaced
    }

    [Fact]
    public async Task Portal_decays_a_stale_confirmation_to_unverified()
    {
        var (portal, health) = BuildPortal();
        await health.RecordOutcomeAsync("health-portal:alice@example.com", alive: true, DateTimeOffset.UtcNow - TimeSpan.FromHours(3));

        var connection = Assert.Single(await portal.ListConnectionsAsync(Admin));

        Assert.Equal("unverified", connection.Status);    // stale green is no green
        Assert.NotNull(connection.LastVerifiedAt);        // still honest about when it was alive
    }

    [Fact]
    public async Task Portal_shows_dead_after_an_unauthorized_call()
    {
        var (portal, health) = BuildPortal();
        await health.RecordOutcomeAsync("health-portal:alice@example.com", alive: false, DateTimeOffset.UtcNow);

        var connection = Assert.Single(await portal.ListConnectionsAsync(Admin));

        Assert.Equal("dead", connection.Status);
    }

    [Fact]
    public async Task Portal_is_unverified_with_no_store_record()
    {
        var (portal, _) = BuildPortal(); // nothing recorded
        var connection = Assert.Single(await portal.ListConnectionsAsync(Admin));
        Assert.Equal("unverified", connection.Status);
    }

    [Fact]
    public async Task Portal_fails_closed_to_unverified_when_the_store_throws()
    {
        var (portal, _) = BuildPortal(new ThrowingHealthStore());
        var connection = Assert.Single(await portal.ListConnectionsAsync(Admin));

        // A flaky metadata store must NEVER be able to paint a connection green.
        Assert.Equal("unverified", connection.Status);
    }

    private sealed class ThrowingHealthStore : IConnectionHealthStore
    {
        public Task<ConnectionHealthRecord?> GetAsync(string connectionKey, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("metadata store is down");

        public Task RecordOutcomeAsync(string connectionKey, bool alive, DateTimeOffset at, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public IReadOnlyList<DegradationEvent> RecentDegradations(int max = 32) => [];
    }
}
