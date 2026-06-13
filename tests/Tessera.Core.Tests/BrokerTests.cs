using Tessera.Core.Audit;
using Tessera.Core.Broker;
using Tessera.Core.Model;
using Tessera.Core.Policy;
using Tessera.Core.Resolution;
using Tessera.Core.Stores;
using Xunit;

namespace Tessera.Core.Tests;

public sealed class BrokerTests
{
    private static (BrokerCore Broker, CountingStore Store, RecordingAudit Audit) Build(params Grant[] grants)
    {
        var store = new CountingStore();
        store.Put("health-portal-session", new CredentialBundle(AccessToken: "tok"));
        var pdp = new PolicyDecisionPoint(grants);
        var resolver = new CredentialResolver(
            [new TargetBinding("health-portal", "health-portal-session", "alice@example.com")],
            store);
        var audit = new RecordingAudit();
        return (new BrokerCore(pdp, resolver, audit), store, audit);
    }

    [Fact]
    public async Task Denied_request_never_touches_the_store()
    {
        var (broker, store, audit) = Build(); // no grants → deny
        var result = await broker.HandleAsync(TestData.Request(onBehalfOf: TestData.VerifiedUser("alice@example.com")));

        Assert.False(result.Decision.Allowed);
        Assert.Null(result.Credential);
        Assert.Equal(0, store.Reads);
        Assert.Single(audit.Entries);
    }

    [Fact]
    public async Task Allowed_request_resolves_status_and_audits()
    {
        var grant = new Grant("spiffe://tessera.local/chatbot", "health-portal", ["read:*"], "alice@example.com");
        var (broker, store, audit) = Build(grant);

        var result = await broker.HandleAsync(TestData.Request(
            caller: TestData.VerifiedCaller("spiffe://tessera.local/chatbot"),
            action: "read:results",
            onBehalfOf: TestData.VerifiedUser("alice@example.com")));

        Assert.True(result.Ok);
        Assert.Equal(CredentialStatus.Present, result.Credential!.Status);
        Assert.Equal(1, store.Reads);
        Assert.Single(audit.Entries);
        Assert.Equal(Effect.Allow, audit.Entries[0].Effect);
    }

    private sealed class CountingStore : ICredentialStore
    {
        private readonly Dictionary<string, CredentialBundle> _bundles = new(StringComparer.Ordinal);
        public int Reads { get; private set; }
        public string Kind => "counting";

        public void Put(string name, CredentialBundle bundle) => _bundles[name] = bundle;

        public Task<CredentialBundle> GetBundleAsync(string name, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(_bundles.TryGetValue(name, out var b) ? b : CredentialBundle.Empty);
        }
    }

    private sealed class RecordingAudit : IAuditSink
    {
        public List<AuditEntry> Entries { get; } = [];

        public void Record(AccessRequest request, Decision decision, ResolvedCredential? credential) =>
            Entries.Add(new AuditEntry(
                DateTimeOffset.UtcNow,
                request.Caller.Id,
                request.Caller.IsVerified,
                request.OnBehalfOf?.Subject,
                request.Target,
                request.Action,
                decision.Effect,
                decision.Reason,
                credential?.Status.ToString()));
    }
}
