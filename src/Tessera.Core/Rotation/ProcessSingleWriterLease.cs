namespace Tessera.Core.Rotation;

/// <summary>
/// The default in-process <see cref="ISingleWriterLease"/>: it always grants (this process
/// assumes it is the sole writer) and issues a strictly-increasing fencing token on each
/// acquisition. This is correct <b>only</b> for a single-replica deployment — the same
/// assumption the operator asserts today with <c>refresh.acknowledgeSingleWriter</c>. It
/// makes the single-writer a real seam (so the rotator gates on an <em>acquired</em> lease,
/// not a boolean) and supplies the fencing token, but it provides no cross-replica mutual
/// exclusion. For genuine multi-replica safety, register a Kubernetes-Lease-backed
/// implementation (ADR 0026; its RBAC is plan-only infra).
/// </summary>
public sealed class ProcessSingleWriterLease : ISingleWriterLease
{
    private long _token;

    /// <inheritdoc />
    public Task<IWriterLeaseHold?> TryAcquireAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IWriterLeaseHold?>(new Hold(Interlocked.Increment(ref _token)));

    private sealed class Hold(long token) : IWriterLeaseHold
    {
        public long FencingToken { get; } = token;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
