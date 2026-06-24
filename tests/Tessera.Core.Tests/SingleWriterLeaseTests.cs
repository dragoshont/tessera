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
    public async Task Process_lease_grants_when_free_and_releases_on_dispose()
    {
        var lease = new ProcessSingleWriterLease();

        long firstToken;
        await using (var hold = await lease.TryAcquireAsync())
        {
            Assert.NotNull(hold);
            firstToken = hold!.FencingToken;
        } // released here

        // Free again ⇒ re-acquirable, with a strictly higher fencing token.
        await using var second = await lease.TryAcquireAsync();
        Assert.NotNull(second);
        Assert.True(second!.FencingToken > firstToken);
    }

    [Fact]
    public async Task Process_lease_denies_a_second_concurrent_holder()
    {
        var lease = new ProcessSingleWriterLease();

        await using var first = await lease.TryAcquireAsync();
        Assert.NotNull(first);

        // While the first hold is alive, a second acquire must be refused — the two
        // in-process writers (rotation pass + read-through refresh) are serialized.
        var second = await lease.TryAcquireAsync();
        Assert.Null(second);
    }

    [Fact]
    public async Task Process_lease_issues_strictly_increasing_fencing_tokens()
    {
        var lease = new ProcessSingleWriterLease();

        long a, b, c;
        await using (var h = await lease.TryAcquireAsync()) { a = h!.FencingToken; }
        await using (var h = await lease.TryAcquireAsync()) { b = h!.FencingToken; }
        await using (var h = await lease.TryAcquireAsync()) { c = h!.FencingToken; }

        // Strictly increasing — a write tagged with a lower token must be refused (fencing).
        Assert.True(b > a);
        Assert.True(c > b);
    }
}
