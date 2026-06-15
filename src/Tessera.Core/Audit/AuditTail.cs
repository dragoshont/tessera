namespace Tessera.Core.Audit;

using Tessera.Core.Model;
using Tessera.Core.Resolution;

/// <summary>
/// A read port over a bounded, in-memory tail of recent audit entries — the source
/// for the portal's activity feed (ADR 0017). It is a <em>convenience mirror</em>
/// of the durable audit sink, never the source of truth: the JSONL sink (stdout →
/// Loki) remains the authoritative, append-only record. The tail is volatile by
/// design (a restart drops it) and bounded by construction (a fixed capacity), so
/// it can never grow without bound or be turned into a query-the-disk amplifier.
/// </summary>
public interface IAuditTail
{
    /// <summary>The maximum number of entries the tail retains (newest-wins).</summary>
    int Capacity { get; }

    /// <summary>
    /// Returns recent entries newest-first, filtered server-side (never a client
    /// concern) and capped at <paramref name="limit"/> (itself capped at
    /// <see cref="Capacity"/>). When <paramref name="onBehalfOf"/> is non-null, only
    /// entries delegated for that exact principal are returned — the self-scope the
    /// portal applies for a member. When <paramref name="since"/> is set, only
    /// entries at or after that instant are returned.
    /// </summary>
    /// <param name="onBehalfOf">The principal to scope to, or <c>null</c> for all entries (operator).</param>
    /// <param name="since">Only entries at/after this instant, or <c>null</c> for no lower bound.</param>
    /// <param name="limit">Maximum entries to return (clamped to 1..<see cref="Capacity"/>).</param>
    IReadOnlyList<AuditEntry> Query(string? onBehalfOf, DateTimeOffset? since, int limit);
}

/// <summary>An always-empty tail (used when the tail is disabled, capacity ≤ 0).</summary>
public sealed class NullAuditTail : IAuditTail
{
    /// <summary>The shared instance.</summary>
    public static readonly NullAuditTail Instance = new();

    /// <inheritdoc/>
    public int Capacity => 0;

    /// <inheritdoc/>
    public IReadOnlyList<AuditEntry> Query(string? onBehalfOf, DateTimeOffset? since, int limit) => [];
}

/// <summary>
/// An <see cref="IAuditSink"/> decorator that retains a bounded, in-memory tail of
/// the most recent entries (newest-wins) while delegating to an inner sink for the
/// durable record. It records to the inner sink <b>first</b>, then mirrors into the
/// ring inside a path that can never throw — so a ring failure can never weaken or
/// reorder the authoritative audit log (ADR 0017, Q3). The ring is fixed-capacity:
/// memory is O(capacity), insertion is O(1), and there is no unbounded growth.
/// </summary>
public sealed class RingBufferAuditSink : IAuditSink, IAuditTail
{
    private readonly IAuditSink _inner;
    private readonly AuditEntry[] _ring;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private int _head;   // index of the next write slot
    private int _count;  // number of entries currently held (≤ capacity)

    /// <summary>Wraps <paramref name="inner"/> with a tail of at most <paramref name="capacity"/> entries.</summary>
    /// <param name="inner">The durable sink to delegate to (written first, always).</param>
    /// <param name="capacity">The maximum entries to retain (must be &gt; 0).</param>
    /// <param name="timeProvider">Clock for the tail entry timestamp (injected for tests); defaults to the system clock.</param>
    public RingBufferAuditSink(IAuditSink inner, int capacity, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _inner = inner;
        _ring = new AuditEntry[capacity];
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public int Capacity => _ring.Length;

    /// <inheritdoc/>
    public void Record(AccessRequest request, Decision decision, ResolvedCredential? credential)
    {
        // Durable first: the authoritative log is never starved by the tail.
        _inner.Record(request, decision, credential);

        // Then mirror into the ring. This must never propagate — a convenience tail
        // failing is not a reason to fail a brokering decision (Q3).
        try
        {
            // Stamp the tail entry from the injected clock so the feed's ordering +
            // since-filter are deterministic (the durable sink keeps its own stamp).
            var entry = AuditEntry.From(request, decision, credential) with { Timestamp = _time.GetUtcNow() };
            lock (_gate)
            {
                _ring[_head] = entry;
                _head = (_head + 1) % _ring.Length;
                if (_count < _ring.Length)
                {
                    _count++;
                }
            }
        }
        catch
        {
            // Intentionally swallowed: the tail is best-effort; the durable sink
            // already has the entry.
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<AuditEntry> Query(string? onBehalfOf, DateTimeOffset? since, int limit)
    {
        if (limit < 1)
        {
            return [];
        }

        var cap = Math.Min(limit, _ring.Length);

        // Snapshot oldest→newest under the lock, then release before filtering.
        AuditEntry[] snapshot;
        lock (_gate)
        {
            snapshot = new AuditEntry[_count];
            // The oldest entry is at (_head - _count) modulo capacity.
            var start = (_head - _count + _ring.Length) % _ring.Length;
            for (var i = 0; i < _count; i++)
            {
                snapshot[i] = _ring[(start + i) % _ring.Length];
            }
        }

        var results = new List<AuditEntry>(Math.Min(cap, snapshot.Length));
        // Walk newest→oldest so the most recent activity is first and the cap keeps
        // the freshest entries.
        for (var i = snapshot.Length - 1; i >= 0 && results.Count < cap; i--)
        {
            var entry = snapshot[i];
            if (since is { } lowerBound && entry.Timestamp < lowerBound)
            {
                continue;
            }

            if (onBehalfOf is not null
                && !string.Equals(entry.OnBehalfOf, onBehalfOf, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            results.Add(entry);
        }

        return results;
    }
}
