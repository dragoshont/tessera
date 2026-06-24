using System.Collections.Concurrent;

namespace Tessera.Core.Health;

/// <summary>
/// The default in-process <see cref="IConnectionHealthStore"/>: a concurrent map of
/// connection key → recorded verdict metadata, plus a bounded ring of recent degradations.
/// Volatile by design — a restart drops it and every connection projects as
/// <c>unverified</c> until the next real call re-earns a verdict (fail-closed, never a
/// stale green). Suitable for the single-replica broker; a durable backend can implement
/// the same interface later without touching the verdict logic.
/// </summary>
public sealed class InMemoryConnectionHealthStore : IConnectionHealthStore
{
    private const int DegradationCapacity = 128;

    private readonly ConcurrentDictionary<string, ConnectionHealthRecord> _records = new(StringComparer.Ordinal);
    // Newest appended last; trimmed to DegradationCapacity. A lock keeps the trim + the
    // ordering consistent — degradations are rare (a death), so contention is negligible.
    private readonly object _degradeGate = new();
    private readonly LinkedList<DegradationEvent> _degradations = new();

    /// <inheritdoc />
    public Task<ConnectionHealthRecord?> GetAsync(string connectionKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionKey);
        return Task.FromResult(_records.TryGetValue(connectionKey, out var record) ? record : null);
    }

    /// <inheritdoc />
    public Task RecordOutcomeAsync(string connectionKey, bool alive, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionKey);

        // Capture the prior verdict to detect a degradation (best-effort: a benign race
        // here only affects the awareness log, never the verdict the portal projects).
        _records.TryGetValue(connectionKey, out var prior);

        _records.AddOrUpdate(
            connectionKey,
            // First observation for this connection.
            _ => alive
                ? new ConnectionHealthRecord(VerifiedAlive: true, LastVerifiedAt: at, ConsecutiveFailures: 0)
                : new ConnectionHealthRecord(VerifiedAlive: false, LastVerifiedAt: null, ConsecutiveFailures: 1),
            // Fold the outcome into the existing record.
            (_, p) => alive
                ? p with { VerifiedAlive = true, LastVerifiedAt = at, ConsecutiveFailures = 0 }
                : p with { VerifiedAlive = false, ConsecutiveFailures = p.ConsecutiveFailures + 1 });

        // A degradation is a transition INTO dead from a non-dead state (live or
        // unverified). dead → dead is not re-reported; that is the same outage, not news.
        if (!alive && prior?.VerifiedAlive != false)
        {
            RecordDegradation(connectionKey, wasAlive: prior?.VerifiedAlive == true, at);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IReadOnlyList<DegradationEvent> RecentDegradations(int max = 32)
    {
        if (max <= 0)
        {
            return [];
        }

        lock (_degradeGate)
        {
            // Newest first.
            var result = new List<DegradationEvent>(Math.Min(max, _degradations.Count));
            for (var node = _degradations.Last; node is not null && result.Count < max; node = node.Previous)
            {
                result.Add(node.Value);
            }

            return result;
        }
    }

    private void RecordDegradation(string connectionKey, bool wasAlive, DateTimeOffset at)
    {
        var (provider, principal) = SplitKey(connectionKey);
        var from = wasAlive ? "live" : "unverified";
        var who = string.IsNullOrEmpty(principal) ? "this session" : principal;
        var what = string.IsNullOrEmpty(provider) ? "the provider" : provider;
        var remediation = $"The session for {who} on {what} is no longer accepted (it went {from} → dead). "
            + "Reconnect it in the portal (the owner re-logs in); brokered calls will fail until it is restored.";

        var evt = new DegradationEvent(connectionKey, provider, principal, from, at, remediation);
        lock (_degradeGate)
        {
            _degradations.AddLast(evt);
            while (_degradations.Count > DegradationCapacity)
            {
                _degradations.RemoveFirst();
            }
        }
    }

    private static (string Provider, string Principal) SplitKey(string connectionKey)
    {
        var i = connectionKey.IndexOf(':', StringComparison.Ordinal);
        return i < 0
            ? (connectionKey, string.Empty)
            : (connectionKey[..i], connectionKey[(i + 1)..]);
    }
}
