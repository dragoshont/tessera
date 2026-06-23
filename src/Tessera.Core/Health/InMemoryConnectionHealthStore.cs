using System.Collections.Concurrent;

namespace Tessera.Core.Health;

/// <summary>
/// The default in-process <see cref="IConnectionHealthStore"/>: a concurrent map of
/// connection key → recorded verdict metadata. Volatile by design — a restart drops it
/// and every connection projects as <c>unverified</c> until the next real call re-earns a
/// verdict (fail-closed, never a stale green). Suitable for the single-replica broker; a
/// durable backend can implement the same interface later without touching the verdict logic.
/// </summary>
public sealed class InMemoryConnectionHealthStore : IConnectionHealthStore
{
    private readonly ConcurrentDictionary<string, ConnectionHealthRecord> _records = new(StringComparer.Ordinal);

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
        _records.AddOrUpdate(
            connectionKey,
            // First observation for this connection.
            _ => alive
                ? new ConnectionHealthRecord(VerifiedAlive: true, LastVerifiedAt: at, ConsecutiveFailures: 0)
                : new ConnectionHealthRecord(VerifiedAlive: false, LastVerifiedAt: null, ConsecutiveFailures: 1),
            // Fold the outcome into the existing record.
            (_, prior) => alive
                ? prior with { VerifiedAlive = true, LastVerifiedAt = at, ConsecutiveFailures = 0 }
                : prior with { VerifiedAlive = false, ConsecutiveFailures = prior.ConsecutiveFailures + 1 });
        return Task.CompletedTask;
    }
}
