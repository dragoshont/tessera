using Microsoft.Extensions.Logging.Abstractions;
using Tessera.Core.Configuration;
using Tessera.Core.Egress;
using Tessera.Core.Rotation;
using Tessera.Core.Stores;
using Tessera.Providers;
using Xunit;

namespace Tessera.Broker.Tests;

/// <summary>
/// SDD-03 — the rotation owner gates each pass on the single-writer lease (ADR 0026):
/// it rotates only while it holds the lease, and stays inert when another replica does.
/// </summary>
public sealed class SessionRefreshServiceTests
{
    private static SessionRefreshOrchestrator EmptyOrchestrator() =>
        new(
            new LoadedPolicy([], [], []), // no recipes ⇒ the pass is a harmless no-op
            new InMemoryCredentialStore(),
            new SessionRefresher(new NoTransport(), new NoWriter(), new SsrfGuard(["api.example.com"])));

    [Fact]
    public async Task Runs_the_pass_while_it_holds_the_lease()
    {
        var service = new SessionRefreshService(
            EmptyOrchestrator(), TimeSpan.FromMinutes(30), NullLogger<SessionRefreshService>.Instance,
            new ProcessSingleWriterLease());

        Assert.True(await service.TryRunPassAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Skips_the_pass_when_another_replica_holds_the_lease()
    {
        var service = new SessionRefreshService(
            EmptyOrchestrator(), TimeSpan.FromMinutes(30), NullLogger<SessionRefreshService>.Instance,
            new DenyingLease());

        // No two writers ever rotate the same single-use session.
        Assert.False(await service.TryRunPassAsync(CancellationToken.None));
    }

    private sealed class DenyingLease : ISingleWriterLease
    {
        public Task<IWriterLeaseHold?> TryAcquireAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IWriterLeaseHold?>(null);
    }

    private sealed class NoTransport : IHttpTransport
    {
        public Task<TransportResponse> SendAsync(string method, string url, IReadOnlyDictionary<string, string> headers, string? body, CancellationToken cancellationToken = default)
            => Task.FromResult(new TransportResponse(200, new Dictionary<string, string>(), ""));
    }

    private sealed class NoWriter : ICredentialWriter
    {
        public Task PutBundleAsync(string name, CredentialBundle bundle, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
