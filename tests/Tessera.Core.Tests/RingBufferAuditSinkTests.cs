using Tessera.Core.Audit;
using Tessera.Core.Identity;
using Tessera.Core.Model;
using Tessera.Core.Resolution;
using Xunit;

namespace Tessera.Core.Tests;

/// <summary>
/// Unit tests for the bounded in-memory activity tail (ADR 0017). They prove the
/// security-relevant invariants directly: the ring is bounded (newest-wins), it
/// scopes to a principal server-side, and — critically — it never starves the
/// durable inner sink (every entry reaches the inner sink even when the ring is full).
/// </summary>
public sealed class RingBufferAuditSinkTests
{
    private static AccessRequest Request(string caller, string? onBehalfOf, string target, string action, string? preferredUsername = null)
    {
        var who = new CallerIdentity(caller, VerificationMethod.OidcJwt, "tessera.local");
        var user = onBehalfOf is null
            ? null
            : new EndUserAssertion(onBehalfOf, "https://issuer.example", VerificationMethod.OidcJwt, preferredUsername);
        return new AccessRequest(who, target, action, user);
    }

    private static void Record(RingBufferAuditSink sink, string caller, string? onBehalfOf, string target, string action, Effect effect = Effect.Allow, string? preferredUsername = null)
    {
        var decision = effect switch
        {
            Effect.Allow => Decision.Allow("granted"),
            Effect.StepUp => Decision.StepUp("confirm", "approve"),
            _ => Decision.Deny("denied"),
        };
        sink.Record(Request(caller, onBehalfOf, target, action, preferredUsername), decision, credential: null);
    }

    [Fact]
    public void Tail_is_bounded_to_capacity_and_keeps_the_newest()
    {
        var ring = new RingBufferAuditSink(NullAuditSink.Instance, capacity: 3);
        foreach (var n in new[] { "a", "b", "c", "d", "e" })
        {
            Record(ring, "caller", "alice@example.com", target: n, action: "read:x");
        }

        var entries = ring.Query(onBehalfOf: null, since: null, limit: 100);

        // Only the newest 3 survive, newest-first.
        Assert.Equal(3, entries.Count);
        Assert.Equal(new[] { "e", "d", "c" }, entries.Select(e => e.Target).ToArray());
    }

    [Fact]
    public void Query_scopes_to_a_principal_server_side()
    {
        var ring = new RingBufferAuditSink(NullAuditSink.Instance, capacity: 10);
        Record(ring, "caller", "alice@example.com", "health-portal", "read:appointments");
        Record(ring, "caller", "bob@example.com", "health-portal", "read:appointments");
        Record(ring, "caller", "alice@example.com", "utility-co", "read:bill");

        var bobOnly = ring.Query(onBehalfOf: "bob@example.com", since: null, limit: 100);

        Assert.Single(bobOnly);
        Assert.Equal("bob@example.com", bobOnly[0].OnBehalfOf);
    }

    [Fact]
    public void Query_scope_is_case_insensitive()
    {
        var ring = new RingBufferAuditSink(NullAuditSink.Instance, capacity: 10);
        Record(ring, "caller", "Alice@Example.com", "health-portal", "read:x");

        var hit = ring.Query(onBehalfOf: "alice@example.com", since: null, limit: 100);

        Assert.Single(hit);
    }

    [Fact]
    public void Query_filters_by_since()
    {
        // A deterministic clock — no Thread.Sleep, no flake: old is stamped strictly
        // before the cut, new strictly after.
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var ring = new RingBufferAuditSink(NullAuditSink.Instance, capacity: 10, timeProvider: clock);

        Record(ring, "caller", "alice@example.com", "old", "read:x");
        clock.Advance(TimeSpan.FromSeconds(1));
        var cut = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromSeconds(1));
        Record(ring, "caller", "alice@example.com", "new", "read:x");

        var afterCut = ring.Query(onBehalfOf: null, since: cut, limit: 100);

        Assert.Single(afterCut);
        Assert.Equal("new", afterCut[0].Target);
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void Query_caps_at_limit_returning_the_newest()
    {
        var ring = new RingBufferAuditSink(NullAuditSink.Instance, capacity: 10);
        foreach (var n in new[] { "a", "b", "c", "d" })
        {
            Record(ring, "caller", "alice@example.com", n, "read:x");
        }

        var capped = ring.Query(onBehalfOf: null, since: null, limit: 2);

        Assert.Equal(2, capped.Count);
        Assert.Equal(new[] { "d", "c" }, capped.Select(e => e.Target).ToArray());
    }

    [Fact]
    public void Inner_sink_receives_every_entry_even_when_the_ring_overflows()
    {
        var inner = new RecordingSink();
        var ring = new RingBufferAuditSink(inner, capacity: 2);

        for (var i = 0; i < 5; i++)
        {
            Record(ring, "caller", "alice@example.com", target: $"t{i}", action: "read:x");
        }

        // The durable inner sink keeps ALL 5; only the volatile ring is bounded to 2.
        Assert.Equal(5, inner.Count);
        Assert.Equal(2, ring.Query(null, null, 100).Count);
    }

    [Fact]
    public void OnBehalfOf_uses_preferred_username_when_present()
    {
        var ring = new RingBufferAuditSink(NullAuditSink.Instance, capacity: 10);
        // subject = an opaque oid, preferred_username = the email the portal scopes by.
        Record(ring, "caller", onBehalfOf: "00000000-oid", target: "health-portal", action: "read:x", preferredUsername: "alice@example.com");

        var byEmail = ring.Query(onBehalfOf: "alice@example.com", since: null, limit: 100);

        Assert.Single(byEmail);
        Assert.Equal("alice@example.com", byEmail[0].OnBehalfOf);
    }

    [Fact]
    public void Capacity_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RingBufferAuditSink(NullAuditSink.Instance, capacity: 0));
    }

    [Fact]
    public void Limit_below_one_returns_empty()
    {
        var ring = new RingBufferAuditSink(NullAuditSink.Instance, capacity: 5);
        Record(ring, "caller", "alice@example.com", "t", "read:x");

        Assert.Empty(ring.Query(null, null, limit: 0));
    }

    private sealed class RecordingSink : IAuditSink
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Record(AccessRequest request, Decision decision, ResolvedCredential? credential) =>
            Interlocked.Increment(ref _count);
    }
}
