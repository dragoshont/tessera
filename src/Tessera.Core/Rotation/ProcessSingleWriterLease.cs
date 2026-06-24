namespace Tessera.Core.Rotation;

/// <summary>
/// The default in-process <see cref="ISingleWriterLease"/>: a real, non-blocking, in-process
/// mutex that issues a strictly-increasing fencing token. At most one hold exists at a time
/// <em>within this process</em>, so the two in-process writers — the rotation pass and the
/// read-through-on-401 refresh — are genuinely serialized and can never refresh the same
/// session concurrently. A second concurrent acquire returns <c>null</c> (the caller stays
/// inert / surfaces the original error).
/// <para>
/// This provides <b>single-process</b> mutual exclusion only — correct for a single-replica
/// deployment (the same assumption the operator asserts with
/// <c>refresh.acknowledgeSingleWriter</c>). It provides <b>no cross-replica</b> exclusion;
/// for that, register a Kubernetes-Lease-backed implementation (ADR 0026; its RBAC is
/// plan-only infra).
/// </para>
/// </summary>
public sealed class ProcessSingleWriterLease : ISingleWriterLease
{
    private int _held; // 0 = free, 1 = held (Interlocked CAS)
    private long _token;

    /// <inheritdoc />
    public Task<IWriterLeaseHold?> TryAcquireAsync(CancellationToken cancellationToken = default)
    {
        // Non-blocking: grant only if currently free, so a concurrent writer gets null and
        // stays inert rather than double-writing the same session.
        if (Interlocked.CompareExchange(ref _held, 1, 0) != 0)
        {
            return Task.FromResult<IWriterLeaseHold?>(null);
        }

        return Task.FromResult<IWriterLeaseHold?>(new Hold(this, Interlocked.Increment(ref _token)));
    }

    private void Release() => Interlocked.Exchange(ref _held, 0);

    private sealed class Hold(ProcessSingleWriterLease owner, long token) : IWriterLeaseHold
    {
        private int _released;

        public long FencingToken { get; } = token;

        public ValueTask DisposeAsync()
        {
            // Idempotent release — disposing the same hold twice frees the lease once.
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
