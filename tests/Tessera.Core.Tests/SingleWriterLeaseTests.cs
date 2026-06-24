using Tessera.Core.Rotation;
using Xunit;

namespace Tessera.Core.Tests;

/// <summary>
/// SDD-03 — the single-writer lease seam (ADR 0026). The in-process default always grants
/// (single-replica correct) and issues a strictly-increasing fencing token, the monotonic
/// guard a real write must carry.
/// </summary>
public sealed class SingleWriterLeaseTests
{
    [Fact]
    public async Task Process_lease_always_grants()
    {
        var lease = new ProcessSingleWriterLease();
        await using var hold = await lease.TryAcquireAsync();
        Assert.NotNull(hold);
    }

    [Fact]
    public async Task Process_lease_issues_strictly_increasing_fencing_tokens()
    {
        var lease = new ProcessSingleWriterLease();

        await using var a = await lease.TryAcquireAsync();
        await using var b = await lease.TryAcquireAsync();
        await using var c = await lease.TryAcquireAsync();

        // Strictly increasing — a write tagged with a lower token must be refused (fencing).
        Assert.True(b!.FencingToken > a!.FencingToken);
        Assert.True(c!.FencingToken > b!.FencingToken);
    }
}
