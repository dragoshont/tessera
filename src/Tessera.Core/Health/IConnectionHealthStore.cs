namespace Tessera.Core.Health;

/// <summary>
/// Stores and retrieves the non-secret, per-connection liveness metadata Tessera
/// learns from real brokered calls. Keyed by the connection identity
/// <c>"{target}:{principal}"</c> — the same key the portal projects.
/// <para>
/// Implementations are operational-metadata stores, <b>never</b> secret stores: a
/// connection's record carries no credential material. A miss returns <c>null</c>
/// ("never observed" ⇒ the projection shows <c>unverified</c>, fail-closed — analysis A5).
/// </para>
/// </summary>
public interface IConnectionHealthStore
{
    /// <summary>
    /// Returns the recorded metadata for <paramref name="connectionKey"/>, or null when
    /// nothing has been observed yet. Must not throw for a missing key (return null).
    /// </summary>
    Task<ConnectionHealthRecord?> GetAsync(string connectionKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the outcome of a real brokered call. <paramref name="alive"/> = true folds in
    /// a confirmed-alive (sets <c>VerifiedAlive=true</c>, <c>LastVerifiedAt=at</c>, resets the
    /// failure counter); = false folds in an unauthorized rejection (sets
    /// <c>VerifiedAlive=false</c>, increments the failure counter, preserves the prior
    /// <c>LastVerifiedAt</c> so the UI can still say "last alive …"). Idempotent per call.
    /// A transition <em>into</em> <c>dead</c> (from <c>live</c> or <c>unverified</c>) is
    /// surfaced via <see cref="RecentDegradations"/> so a death is seen, not silent (A6).
    /// </summary>
    Task RecordOutcomeAsync(string connectionKey, bool alive, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>
    /// The most recent degradations (newest first), bounded. The awareness surface reads
    /// this to show "what just broke" — the proactive-degradation signal that was missing
    /// when the RM session died unseen. Secret-free identity metadata only.
    /// </summary>
    IReadOnlyList<DegradationEvent> RecentDegradations(int max = 32);
}
