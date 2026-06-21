using Tessera.Core.Egress;
using Tessera.Core.Identity;
using Xunit;

namespace Tessera.Core.Tests;

/// <summary>
/// Unit tests for the held-write challenge store (ADR 0023) — the security properties that
/// make out-of-band write confirmation real: issuance is idempotent by content, a write
/// cannot proceed without approval, an approval is single-use + content-bound + expiring, and
/// only the bound person may decide. These are the invariants the egress gate relies on.
/// </summary>
public sealed class WriteChallengeStoreTests
{
    private static readonly CallerIdentity Caller = new("apple-mcp", VerificationMethod.OidcJwt);
    private static readonly EndUserAssertion Alice =
        new("alice-oid", "https://issuer.example", VerificationMethod.OidcJwt, "alice@example.com");

    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static PendingWrite Candidate(
        string principal = "alice@example.com",
        string target = "apple-caldav",
        string hash = "h1",
        DateTimeOffset? now = null,
        int ttlSeconds = 600)
    {
        var created = now ?? T0;
        return new PendingWrite
        {
            Id = Guid.NewGuid().ToString("N"),
            Caller = Caller,
            OnBehalfOf = Alice,
            Principal = principal,
            Target = target,
            Action = "manage:dav",
            Method = "PUT",
            PathAndQuery = "/123/calendars/x.ics",
            ContentHash = hash,
            UpstreamHost = "caldav.icloud.com",
            Summary = "Create event 'Dentist'",
            BodyExcerpt = "BEGIN:VEVENT...",
            CreatedAt = created,
            ExpiresAt = created.AddSeconds(ttlSeconds),
        };
    }

    [Fact]
    public void Issue_is_idempotent_by_content_while_pending()
    {
        var store = new InMemoryWriteChallengeStore(16);
        var a = store.IssueOrGet(Candidate(hash: "h1"), T0);
        var b = store.IssueOrGet(Candidate(hash: "h1"), T0);
        Assert.Equal(a.Id, b.Id); // the same held write yields one stable challenge, not a pile
    }

    [Fact]
    public void Issue_distinguishes_different_content()
    {
        var store = new InMemoryWriteChallengeStore(16);
        var a = store.IssueOrGet(Candidate(hash: "h1"), T0);
        var b = store.IssueOrGet(Candidate(hash: "h2"), T0);
        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void A_pending_write_cannot_be_consumed_without_approval()
    {
        var store = new InMemoryWriteChallengeStore(16);
        store.IssueOrGet(Candidate(hash: "h1"), T0);
        Assert.Null(store.TryConsumeApproved("alice@example.com", "apple-caldav", "h1", T0));
    }

    [Fact]
    public void An_approved_write_is_consumed_exactly_once()
    {
        var store = new InMemoryWriteChallengeStore(16);
        var issued = store.IssueOrGet(Candidate(hash: "h1"), T0);
        Assert.NotNull(store.Decide(issued.Id, "alice@example.com", approve: true, T0));
        Assert.NotNull(store.TryConsumeApproved("alice@example.com", "apple-caldav", "h1", T0));
        Assert.Null(store.TryConsumeApproved("alice@example.com", "apple-caldav", "h1", T0)); // single-use
    }

    [Fact]
    public void A_denied_write_cannot_be_consumed()
    {
        var store = new InMemoryWriteChallengeStore(16);
        var issued = store.IssueOrGet(Candidate(hash: "h1"), T0);
        Assert.NotNull(store.Decide(issued.Id, "alice@example.com", approve: false, T0));
        Assert.Null(store.TryConsumeApproved("alice@example.com", "apple-caldav", "h1", T0));
    }

    [Fact]
    public void Only_the_bound_principal_may_decide()
    {
        var store = new InMemoryWriteChallengeStore(16);
        var issued = store.IssueOrGet(Candidate(principal: "alice@example.com", hash: "h1"), T0);
        Assert.Null(store.Decide(issued.Id, "bob@example.com", approve: true, T0)); // not bob's to approve
        Assert.Null(store.TryConsumeApproved("alice@example.com", "apple-caldav", "h1", T0)); // stays unapproved
    }

    [Fact]
    public void An_approved_write_cannot_be_swapped_for_different_content()
    {
        var store = new InMemoryWriteChallengeStore(16);
        var issued = store.IssueOrGet(Candidate(hash: "approved-hash"), T0);
        store.Decide(issued.Id, "alice@example.com", approve: true, T0);
        // A re-request with DIFFERENT content cannot ride the approval.
        Assert.Null(store.TryConsumeApproved("alice@example.com", "apple-caldav", "other-hash", T0));
    }

    [Fact]
    public void An_expired_approved_write_cannot_be_consumed()
    {
        var store = new InMemoryWriteChallengeStore(16);
        var issued = store.IssueOrGet(Candidate(hash: "h1", ttlSeconds: 60), T0);
        store.Decide(issued.Id, "alice@example.com", approve: true, T0);
        var pastTtl = T0.AddSeconds(120);
        Assert.Null(store.TryConsumeApproved("alice@example.com", "apple-caldav", "h1", pastTtl));
    }

    [Fact]
    public void Expired_pending_cannot_be_decided()
    {
        var store = new InMemoryWriteChallengeStore(16);
        var issued = store.IssueOrGet(Candidate(hash: "h1", ttlSeconds: 60), T0);
        Assert.Null(store.Decide(issued.Id, "alice@example.com", approve: true, T0.AddSeconds(120)));
    }

    [Fact]
    public void ListActive_is_scoped_to_the_principal()
    {
        var store = new InMemoryWriteChallengeStore(16);
        store.IssueOrGet(Candidate(principal: "alice@example.com", hash: "h1"), T0);
        store.IssueOrGet(Candidate(principal: "bob@example.com", hash: "h2"), T0);
        var alice = store.ListActive("alice@example.com", T0);
        Assert.Single(alice);
        Assert.Equal("alice@example.com", alice[0].Principal);
    }

    [Fact]
    public void The_store_stays_bounded()
    {
        var store = new InMemoryWriteChallengeStore(4);
        for (var i = 0; i < 20; i++)
        {
            store.IssueOrGet(Candidate(principal: $"u{i}@example.com", hash: $"h{i}"), T0.AddSeconds(i));
        }

        // Never exceeds capacity across all principals (eviction kept it bounded).
        var total = 0;
        for (var i = 0; i < 20; i++)
        {
            total += store.ListActive($"u{i}@example.com", T0.AddSeconds(100)).Count;
        }

        Assert.True(total <= 4, $"store grew to {total}, exceeding capacity 4");
    }

    [Fact]
    public void Hash_binds_the_upstream_url_not_just_method_and_body()
    {
        // The swap-after-approve guard: a different resource yields a different challenge even
        // with an identical (empty) body + method + control headers (judge B1).
        var a = WriteChallengeHash.Compute("DELETE", new Uri("https://caldav.icloud.com/cal/A.ics"), "", default);
        var b = WriteChallengeHash.Compute("DELETE", new Uri("https://caldav.icloud.com/cal/B.ics"), "", default);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Hash_binds_the_webdav_control_headers()
    {
        var src = new Uri("https://caldav.icloud.com/cal/A.ics");
        // A re-pointed MOVE/COPY destination is a different write (judge B1).
        Assert.NotEqual(
            WriteChallengeHash.Compute("MOVE", src, "https://caldav.icloud.com/cal/B.ics\nF\n", default),
            WriteChallengeHash.Compute("MOVE", src, "https://caldav.icloud.com/cal/C.ics\nF\n", default));
        // A widened collection depth (shallow vs deep) is a different write (judge C5).
        Assert.NotEqual(
            WriteChallengeHash.Compute("COPY", src, "https://caldav.icloud.com/cal/B/\nF\n0", default),
            WriteChallengeHash.Compute("COPY", src, "https://caldav.icloud.com/cal/B/\nF\ninfinity", default));
    }
}
