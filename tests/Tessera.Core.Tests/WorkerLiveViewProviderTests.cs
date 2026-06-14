using Tessera.Core.Portal;
using Xunit;

namespace Tessera.Core.Tests;

/// <summary>
/// Unit tests for the worker-backed live-view provider (ADR 0016 §3 / Job A). They
/// prove the broker-side handle contract over a fake browser worker: a successful
/// arm yields a short, identity-bound handle; every worker failure is fail-closed
/// (Unavailable, never a faked session); and the cookie is never part of any result.
/// </summary>
public sealed class WorkerLiveViewProviderTests
{
    private const string Conn = "health-portal:alice@example.com";
    private const string Principal = "alice@example.com";

    private sealed class FakeWorker(Func<LiveViewWorkerRequest, WorkerLiveViewSession?> arm) : ILiveViewWorker
    {
        public LiveViewWorkerRequest? LastRequest { get; private set; }

        public Task<WorkerLiveViewSession?> ArmAsync(LiveViewWorkerRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(arm(request));
        }
    }

    private sealed class ThrowingWorker(Exception ex) : ILiveViewWorker
    {
        public Task<WorkerLiveViewSession?> ArmAsync(LiveViewWorkerRequest request, CancellationToken cancellationToken = default) =>
            throw ex;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task A_successful_arm_yields_an_identity_bound_handle_with_a_short_expiry()
    {
        var now = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);
        var worker = new FakeWorker(_ => new WorkerLiveViewSession(
            LiveViewUrl: "https://worker.internal/s/opaque-token",
            TargetHostname: "portal.example-health.com",
            TtlSeconds: 240,
            ReadWrite: true,
            FaviconUrl: "https://worker.internal/favicon"));
        var provider = new WorkerLiveViewProvider(worker, defaultTtlSeconds: 300, timeProvider: new FixedClock(now));

        var result = await provider.RequestAsync(Conn, Principal);

        Assert.True(result.Issued);
        var handle = result.Handle!;
        Assert.Equal("https://worker.internal/s/opaque-token", handle.LiveViewUrl);
        Assert.Equal("portal.example-health.com", handle.TargetHostname);
        Assert.Equal(LiveViewMode.ReadWrite, handle.Mode);
        Assert.Equal(240, handle.SessionTtlSeconds);                       // worker-pinned ttl wins
        Assert.Equal(now.AddSeconds(240), handle.ExpiresAt);              // absolute expiry from the clock
        Assert.Equal("https://worker.internal/favicon", handle.FaviconUrl);

        // Identity-bound: the worker is asked to arm for THIS principal + connection.
        Assert.Equal(Principal, worker.LastRequest!.Principal);
        Assert.Equal(Conn, worker.LastRequest!.ConnectionId);
        Assert.Equal("health-portal", worker.LastRequest!.Provider);     // provider parsed from the id
    }

    [Fact]
    public async Task The_default_ttl_is_used_when_the_worker_does_not_pin_one()
    {
        var now = new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);
        var worker = new FakeWorker(_ => new WorkerLiveViewSession(
            LiveViewUrl: "https://worker.internal/s/x", TargetHostname: "host.example", TtlSeconds: null));
        var provider = new WorkerLiveViewProvider(worker, defaultTtlSeconds: 180, timeProvider: new FixedClock(now));

        var handle = (await provider.RequestAsync(Conn, Principal)).Handle!;

        Assert.Equal(180, handle.SessionTtlSeconds);
        Assert.Equal(now.AddSeconds(180), handle.ExpiresAt);
    }

    [Fact]
    public async Task A_readonly_session_maps_to_readonly_mode()
    {
        var worker = new FakeWorker(_ => new WorkerLiveViewSession(
            LiveViewUrl: "https://worker.internal/s/x", TargetHostname: "host.example", ReadWrite: false));
        var provider = new WorkerLiveViewProvider(worker);

        var handle = (await provider.RequestAsync(Conn, Principal)).Handle!;

        Assert.Equal(LiveViewMode.ReadOnly, handle.Mode);
    }

    [Fact]
    public async Task A_null_session_is_fail_closed()
    {
        var provider = new WorkerLiveViewProvider(new FakeWorker(_ => null));

        var result = await provider.RequestAsync(Conn, Principal);

        Assert.False(result.Issued);
        Assert.Null(result.Handle);
        Assert.Contains("no live session", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_session_missing_url_or_hostname_is_fail_closed()
    {
        var noUrl = new WorkerLiveViewProvider(new FakeWorker(_ =>
            new WorkerLiveViewSession(LiveViewUrl: "", TargetHostname: "host.example")));
        Assert.False((await noUrl.RequestAsync(Conn, Principal)).Issued);

        var noHost = new WorkerLiveViewProvider(new FakeWorker(_ =>
            new WorkerLiveViewSession(LiveViewUrl: "https://worker/x", TargetHostname: "")));
        Assert.False((await noHost.RequestAsync(Conn, Principal)).Issued);
    }

    [Fact]
    public async Task A_throwing_worker_is_fail_closed_not_propagated()
    {
        var provider = new WorkerLiveViewProvider(new ThrowingWorker(new InvalidOperationException("slot crashed")));

        var result = await provider.RequestAsync(Conn, Principal);

        Assert.False(result.Issued);
        Assert.Contains("could not arm", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_caller_cancellation_still_propagates()
    {
        var provider = new WorkerLiveViewProvider(new ThrowingWorker(new OperationCanceledException()));

        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.RequestAsync(Conn, Principal));
    }

    [Fact]
    public async Task A_blank_connection_or_principal_is_fail_closed_without_calling_the_worker()
    {
        var worker = new FakeWorker(_ => new WorkerLiveViewSession("https://worker/x", "host.example"));
        var provider = new WorkerLiveViewProvider(worker);

        Assert.False((await provider.RequestAsync("", Principal)).Issued);
        Assert.False((await provider.RequestAsync(Conn, "  ")).Issued);
        Assert.Null(worker.LastRequest);   // never reached the worker
    }

    [Fact]
    public async Task No_result_ever_carries_a_secret_value()
    {
        // The worker contract carries no cookie; assert the handle surface is url +
        // hostname + favicon only (presence-of-secret would be a contract break).
        var worker = new FakeWorker(_ => new WorkerLiveViewSession(
            "https://worker.internal/s/opaque", "host.example", TtlSeconds: 120));
        var provider = new WorkerLiveViewProvider(worker);

        var serialized = System.Text.Json.JsonSerializer.Serialize(await provider.RequestAsync(Conn, Principal));

        // The only URL present is the opaque worker handle; no cookie/token field exists.
        Assert.DoesNotContain("cookie", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token\":", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ctor_guards_against_a_non_positive_ttl()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WorkerLiveViewProvider(new FakeWorker(_ => null), defaultTtlSeconds: 0));
    }
}
