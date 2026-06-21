namespace Tessera.Core.Egress;

using System.Security.Cryptography;
using System.Text;
using Tessera.Core.Identity;

/// <summary>The lifecycle of a held write (ADR 0023): a manage-plane request waits
/// <see cref="Pending"/> for an out-of-band human decision, becomes <see cref="Approved"/>
/// or <see cref="Denied"/> in the portal, and is <see cref="Consumed"/> exactly once when
/// the caller re-issues the identical write. An untouched challenge lapses to
/// <see cref="Expired"/> at its TTL.</summary>
public enum WriteChallengeStatus
{
    /// <summary>Awaiting a human decision in the portal.</summary>
    Pending,

    /// <summary>A human approved it out-of-band; the caller may complete the identical write once.</summary>
    Approved,

    /// <summary>A human refused it; the write must never proceed.</summary>
    Denied,

    /// <summary>The approved write was forwarded (single-use); it can never be replayed.</summary>
    Consumed,

    /// <summary>The TTL lapsed before a decision (or before completion); the caller must re-request.</summary>
    Expired,
}

/// <summary>
/// A manage-plane (write) request held for OUT-OF-BAND human approval — the
/// resolution of HL-18 (ADR 0023). The forgeable <c>X-Tessera-Confirm</c> header is
/// gone: Tessera issues this challenge, a human approves it in the portal (a channel
/// the calling MCP/agent cannot drive), and only then may the caller complete the
/// <em>identical</em> write (matched by <see cref="ContentHash"/>, consumed once).
/// </summary>
/// <remarks>
/// Bound to the verified <see cref="OnBehalfOf"/> (never a caller-controlled value),
/// so only the person the write is for can approve it. Carries no credential and no
/// upstream path beyond the host — the body is held only as a capped, operator-facing
/// excerpt so the approver can see what they are confirming. Secret-free by construction.
/// </remarks>
public sealed record PendingWrite
{
    /// <summary>The single-use challenge id (an unguessable token shown to the human + the caller).</summary>
    public required string Id { get; init; }

    /// <summary>The verified workload that requested the write (for audit + reconstruction).</summary>
    public required CallerIdentity Caller { get; init; }

    /// <summary>The verified end-user the write is for — the ONLY principal allowed to approve it.</summary>
    public required EndUserAssertion OnBehalfOf { get; init; }

    /// <summary>The human-readable principal of <see cref="OnBehalfOf"/> (preferred_username ?? subject),
    /// the key grants/portal/audit use and the approver this challenge is scoped to.</summary>
    public required string Principal { get; init; }

    /// <summary>The proxy target (e.g. <c>apple-caldav</c>).</summary>
    public required string Target { get; init; }

    /// <summary>The manage-plane action (e.g. <c>manage:dav</c>).</summary>
    public required string Action { get; init; }

    /// <summary>The HTTP/WebDAV method of the held write.</summary>
    public required string Method { get; init; }

    /// <summary>The upstream resource path + query being mutated (e.g. the iCloud object path) —
    /// what the human verifies in the portal, not the constant <c>/v1/egress/{target}</c> route.</summary>
    public required string PathAndQuery { get; init; }

    /// <summary>The SHA-256 over (method, path+query, body) — the binding that prevents a
    /// post-approval swap. A re-request only completes if its content hashes identically.</summary>
    public required string ContentHash { get; init; }

    /// <summary>The validated upstream host (no path/credential) — context for the approver.</summary>
    public required string UpstreamHost { get; init; }

    /// <summary>A caller-provided, clearly-untrusted one-line summary for readability
    /// (shown alongside the authoritative <see cref="BodyExcerpt"/>, never instead of it).</summary>
    public required string Summary { get; init; }

    /// <summary>A capped excerpt of the raw request body — the source of truth the approver
    /// verifies (the caller's <see cref="Summary"/> could lie; the bytes cannot).</summary>
    public required string BodyExcerpt { get; init; }

    /// <summary>When the challenge was issued.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the challenge lapses (TTL); after this it cannot be approved or consumed.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>The current lifecycle state.</summary>
    public WriteChallengeStatus Status { get; init; } = WriteChallengeStatus.Pending;

    /// <summary>Who decided it (the approving/denying principal), or null while pending.</summary>
    public string? DecidedBy { get; init; }

    /// <summary>When it was decided, or null while pending.</summary>
    public DateTimeOffset? DecidedAt { get; init; }

    /// <summary>True if the challenge has lapsed at <paramref name="now"/> (and is not already a terminal state).</summary>
    public bool IsExpiredAt(DateTimeOffset now) =>
        Status is WriteChallengeStatus.Pending or WriteChallengeStatus.Approved && now >= ExpiresAt;
}

/// <summary>Computes the content hash that binds a challenge to one exact request, so an
/// approved write cannot be swapped for a different one before it completes.</summary>
public static class WriteChallengeHash
{
    /// <summary>SHA-256 (lower-hex) over the method, the validated UPSTREAM absolute URL (the
    /// resource being mutated), a canonical <paramref name="davControl"/> string of the WebDAV
    /// control headers that change which resource / how much a write affects (Destination,
    /// Overwrite, Depth), and the raw body bytes. Binding the upstream URL + control headers — not
    /// the constant <c>/v1/egress/{target}</c> route — is what stops an approved write from being
    /// re-pointed at a different object/host or widened (deep vs shallow) on the re-request.
    /// Identity (onBehalfOf) + target are separate key dimensions, not hashed.</summary>
    public static string Compute(string method, Uri upstream, string davControl, ReadOnlySpan<byte> body)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.UTF8.GetBytes(method.ToUpperInvariant()));
        sha.AppendData("\n"u8);
        sha.AppendData(Encoding.UTF8.GetBytes(upstream.AbsoluteUri));
        sha.AppendData("\n"u8);
        sha.AppendData(Encoding.UTF8.GetBytes(davControl));
        sha.AppendData("\n"u8);
        sha.AppendData(body);
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}

/// <summary>
/// A bounded, in-memory store of held writes (ADR 0023). Issuance is idempotent by
/// (principal, target, contentHash) so repeated attempts before approval don't pile up;
/// approval is scoped to the bound principal; completion is single-use and content-bound.
/// </summary>
public interface IWriteChallengeStore
{
    /// <summary>Issue a challenge for <paramref name="candidate"/>, or return the existing
    /// pending, non-expired challenge for the same (principal, target, contentHash) — so a
    /// caller polling before approval keeps one stable challenge id.</summary>
    PendingWrite IssueOrGet(PendingWrite candidate, DateTimeOffset now);

    /// <summary>Look up a challenge by id (lapsing it to Expired if its TTL passed), or null.</summary>
    PendingWrite? Lookup(string id, DateTimeOffset now);

    /// <summary>List non-expired challenges, newest-first, scoped to <paramref name="principal"/>
    /// when non-null (a member's own), or all (operator).</summary>
    IReadOnlyList<PendingWrite> ListActive(string? principal, DateTimeOffset now);

    /// <summary>Approve or deny challenge <paramref name="id"/> — only if it is still pending,
    /// not expired, and owned by <paramref name="approverPrincipal"/>. Returns the updated
    /// record, or null if not found / not owned / not pending.</summary>
    PendingWrite? Decide(string id, string approverPrincipal, bool approve, DateTimeOffset now);

    /// <summary>On a re-request: atomically find and consume (single-use) the APPROVED,
    /// non-expired challenge matching (principal, target, contentHash). Returns the consumed
    /// record, or null if there is no such approved challenge.</summary>
    PendingWrite? TryConsumeApproved(string principal, string target, string contentHash, DateTimeOffset now);
}

/// <summary>
/// The in-memory <see cref="IWriteChallengeStore"/>: a capacity-bounded dictionary keyed by
/// challenge id, guarded by a single lock (write-confirmation is low-frequency — human-paced —
/// so an O(n) scan over a small capacity is ample). Volatile by design: a broker restart drops
/// pending challenges (they fail safe — the caller simply re-requests + re-approves), it never
/// leaks them. The store holds NO credential bytes and no upstream path.
/// </summary>
public sealed class InMemoryWriteChallengeStore : IWriteChallengeStore
{
    private readonly Dictionary<string, PendingWrite> _byId;
    private readonly int _capacity;
    private readonly Lock _gate = new();

    /// <summary>Creates a store retaining at most <paramref name="capacity"/> challenges (oldest evicted first).</summary>
    public InMemoryWriteChallengeStore(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _byId = new Dictionary<string, PendingWrite>(capacity);
    }

    /// <inheritdoc/>
    public PendingWrite IssueOrGet(PendingWrite candidate, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (_gate)
        {
            Prune(now);
            foreach (var existing in _byId.Values)
            {
                if (existing.Status == WriteChallengeStatus.Pending
                    && now < existing.ExpiresAt
                    && string.Equals(existing.Principal, candidate.Principal, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.Target, candidate.Target, StringComparison.Ordinal)
                    && string.Equals(existing.ContentHash, candidate.ContentHash, StringComparison.Ordinal))
                {
                    return existing; // idempotent: one stable challenge per identical pending write
                }
            }

            EnsureCapacityForOne(now);
            _byId[candidate.Id] = candidate;
            return candidate;
        }
    }

    /// <inheritdoc/>
    public PendingWrite? Lookup(string id, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var entry))
            {
                return null;
            }

            if (entry.IsExpiredAt(now))
            {
                entry = entry with { Status = WriteChallengeStatus.Expired };
                _byId[id] = entry;
            }

            return entry;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<PendingWrite> ListActive(string? principal, DateTimeOffset now)
    {
        lock (_gate)
        {
            Prune(now);
            return _byId.Values
                .Where(e => e.Status is WriteChallengeStatus.Pending or WriteChallengeStatus.Approved)
                .Where(e => principal is null
                    || string.Equals(e.Principal, principal, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.CreatedAt)
                .ToArray();
        }
    }

    /// <inheritdoc/>
    public PendingWrite? Decide(string id, string approverPrincipal, bool approve, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var entry))
            {
                return null;
            }

            if (entry.IsExpiredAt(now))
            {
                _byId[id] = entry with { Status = WriteChallengeStatus.Expired };
                return null;
            }

            // Only the bound principal may decide — an operator cannot approve another
            // person's write, and the calling agent has no portal identity at all.
            if (entry.Status != WriteChallengeStatus.Pending
                || !string.Equals(entry.Principal, approverPrincipal, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var decided = entry with
            {
                Status = approve ? WriteChallengeStatus.Approved : WriteChallengeStatus.Denied,
                DecidedBy = approverPrincipal,
                DecidedAt = now,
            };
            _byId[id] = decided;
            return decided;
        }
    }

    /// <inheritdoc/>
    public PendingWrite? TryConsumeApproved(string principal, string target, string contentHash, DateTimeOffset now)
    {
        lock (_gate)
        {
            foreach (var entry in _byId.Values)
            {
                if (entry.Status == WriteChallengeStatus.Approved
                    && now < entry.ExpiresAt
                    && string.Equals(entry.Principal, principal, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(entry.Target, target, StringComparison.Ordinal)
                    && string.Equals(entry.ContentHash, contentHash, StringComparison.Ordinal))
                {
                    var consumed = entry with { Status = WriteChallengeStatus.Consumed };
                    _byId[entry.Id] = consumed;
                    return consumed;
                }
            }

            return null;
        }
    }

    // Lapse expired pending/approved entries (caller holds _gate).
    private void Prune(DateTimeOffset now)
    {
        List<string>? toExpire = null;
        foreach (var (id, entry) in _byId)
        {
            if (entry.IsExpiredAt(now))
            {
                (toExpire ??= []).Add(id);
            }
        }

        if (toExpire is null)
        {
            return;
        }

        foreach (var id in toExpire)
        {
            _byId[id] = _byId[id] with { Status = WriteChallengeStatus.Expired };
        }
    }

    // Keep the store bounded: drop terminal (consumed/denied/expired) entries first, then the
    // oldest remaining, so a flood of pending writes can never grow memory without bound (caller holds _gate).
    private void EnsureCapacityForOne(DateTimeOffset now)
    {
        if (_byId.Count < _capacity)
        {
            return;
        }

        string? victim = null;
        DateTimeOffset oldest = DateTimeOffset.MaxValue;
        var terminalFound = false;
        foreach (var (id, entry) in _byId)
        {
            var terminal = entry.Status
                is WriteChallengeStatus.Consumed or WriteChallengeStatus.Denied or WriteChallengeStatus.Expired
                || entry.IsExpiredAt(now);

            // Prefer evicting a terminal entry; among the same class prefer the oldest.
            if (terminal && !terminalFound)
            {
                victim = id;
                oldest = entry.CreatedAt;
                terminalFound = true;
            }
            else if (terminal == terminalFound && entry.CreatedAt < oldest)
            {
                victim = id;
                oldest = entry.CreatedAt;
            }
        }

        if (victim is not null)
        {
            _byId.Remove(victim);
        }
    }
}
