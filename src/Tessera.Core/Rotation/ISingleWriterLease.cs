namespace Tessera.Core.Rotation;

/// <summary>
/// A held single-writer lease for the Mode U rotation owner (ADR 0026), carrying a
/// <b>monotonic fencing token</b> (Kleppmann, <i>How to do distributed locking</i>). A
/// lease alone is unsafe — a paused or partitioned holder can act <em>after</em> its lease
/// expired while a new holder is also acting — so the safe contract is that every write
/// performed under the lease carries this token and the resource <b>rejects any write whose
/// token went backwards</b>. The token is strictly increasing across acquisitions.
/// </summary>
public interface IWriterLeaseHold : IAsyncDisposable
{
    /// <summary>
    /// The strictly-increasing fencing token for this hold. A rotation write tagged with a
    /// token lower than the highest the store has already accepted must be refused (the
    /// store-side enforcement is the defense-in-depth layer; the lease is the primary guard).
    /// </summary>
    long FencingToken { get; }
}

/// <summary>
/// The single-writer guarantee for session rotation (ADR 0026). Two replicas both rotating
/// the same single-use session would corrupt it; today that is prevented only by the
/// operator's <c>refresh.acknowledgeSingleWriter</c> honor-system assertion. This seam
/// replaces the assertion with an acquired lease: the rotation pass runs <b>only</b> while
/// this process holds the lease, and the hold's fencing token guards the write.
/// <para>
/// The real implementation is a Kubernetes <c>Lease</c> (etcd/Raft consensus — a proper
/// consensus store, as Kleppmann prescribes for correctness), so exactly one replica ever
/// holds it. The default <see cref="ProcessSingleWriterLease"/> is correct only for a
/// single-replica deployment; the Kubernetes-backed lease + its RBAC are plan-only infra.
/// </para>
/// </summary>
public interface ISingleWriterLease
{
    /// <summary>
    /// Tries to acquire or renew the lease for this process. Returns the hold (with its
    /// fencing token) when this process is the writer, or <c>null</c> when another replica
    /// holds it — in which case the caller must <b>not</b> rotate (stay inert this pass).
    /// </summary>
    Task<IWriterLeaseHold?> TryAcquireAsync(CancellationToken cancellationToken = default);
}
