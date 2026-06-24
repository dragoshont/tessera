using Tessera.Core.Egress;
using Tessera.Core.Health;
using Tessera.Core.Identity;
using Tessera.Core.Policy;
using Tessera.Core.Recipes;
using Tessera.Core.Resolution;
using Tessera.Core.Results;
using Tessera.Core.Rotation;
using Tessera.Core.Stores;
using Xunit;

namespace Tessera.Providers.Tests;

/// <summary>
/// SDD-05 — read-through-on-401. An unauthorized read refreshes the session once under the
/// single-writer lease and retries, so a rotated-out session self-heals. Off by default;
/// the refresh is a rotation write, gated by the lease (ADR 0026, cross-phase analysis A1).
/// </summary>
public sealed class ProviderEgressReadThroughTests
{
    private const string Caller = "chat://librechat";
    private const string User = "alice@example.com";
    private const string Target = "portal";
    private const string Host = "api.example.com";

    private static Recipe RefreshablePortal() => new(
        Target: Target,
        Egress: EgressMode.Http,
        UpstreamBaseUrl: $"https://{Host}/v1",
        Injection: InjectionKind.Cookies,
        Tools: [new RecipeTool("portal_list_items", "GET", "items", "read:items", StepUp: false, "List items")],
        Rotation: new RecipeRotation("tessera"),
        Refresh: new RefreshSpec("refresh"));

    private static ProviderEgress Build(
        IHttpTransport transport, WritableMemoryStore store, ISingleWriterLease lease, bool readThrough, IConnectionHealthStore? health = null)
    {
        var pdp = new PolicyDecisionPoint([new Grant(Caller, Target, ["read:*"], User)]);
        var resolver = new CredentialResolver([new TargetBinding(Target, "portal-alice", User)], store);
        var guard = new SsrfGuard([Host]);
        var refresher = new SessionRefresher(transport, store, guard);
        return new ProviderEgress(
            new PolicyDecisionPointAdapter(pdp.Evaluate), resolver, [RefreshablePortal()], guard, transport,
            writer: store, health: health, refresher: refresher, lease: lease, readThroughOn401: readThrough);
    }

    private static CallerIdentity ChatCaller() => new(Caller, VerificationMethod.Network);
    private static EndUserAssertion Alice() => new(User, "https://issuer/v2.0", VerificationMethod.OidcJwt, User);

    [Fact]
    public async Task A_401_refreshes_under_the_lease_and_retries_to_success()
    {
        var store = new WritableMemoryStore();
        store.Seed("portal-alice", new CredentialBundle(Cookies: new Dictionary<string, string> { ["session"] = "OLD" }));
        // data → 401, refresh → 200 (rotates), retry → 200.
        var transport = new SequenceTransport(
            new TransportResponse(401, new Dictionary<string, string>(), "unauthorized"),
            new TransportResponse(200, new Dictionary<string, string>(), "{\"access_token\":\"NEW_AT\"}"),
            new TransportResponse(200, new Dictionary<string, string>(), "{\"ok\":true}"));
        var health = new InMemoryConnectionHealthStore();
        var egress = Build(transport, store, new ProcessSingleWriterLease(), readThrough: true, health);

        var result = await egress.CallAsync(ChatCaller(), Alice(), Target, "portal_list_items", null, confirmed: false);

        Assert.Equal(ProviderCallStatus.Completed, result.Status);
        Assert.Equal(200, result.HttpStatus);                 // the retry succeeded
        Assert.Equal(3, transport.Calls);                     // data → refresh → retry
        Assert.Equal("NEW_AT", store.Get("portal-alice")!.AccessToken); // session self-healed
        Assert.True((await health.GetAsync($"{Target}:{User}"))!.VerifiedAlive); // verdict reflects the heal
    }

    [Fact]
    public async Task A_401_is_not_refreshed_when_another_replica_holds_the_lease()
    {
        var store = new WritableMemoryStore();
        store.Seed("portal-alice", new CredentialBundle(Cookies: new Dictionary<string, string> { ["session"] = "OLD" }));
        var transport = new SequenceTransport(new TransportResponse(401, new Dictionary<string, string>(), "unauthorized"));
        var egress = Build(transport, store, new DenyingLease(), readThrough: true);

        var result = await egress.CallAsync(ChatCaller(), Alice(), Target, "portal_list_items", null, confirmed: false);

        Assert.Equal(401, result.HttpStatus);   // original surfaced
        Assert.Equal(1, transport.Calls);        // no refresh, no retry — single-writer respected
    }

    [Fact]
    public async Task A_401_is_surfaced_unchanged_when_read_through_is_off()
    {
        var store = new WritableMemoryStore();
        store.Seed("portal-alice", new CredentialBundle(Cookies: new Dictionary<string, string> { ["session"] = "OLD" }));
        var transport = new SequenceTransport(new TransportResponse(401, new Dictionary<string, string>(), "unauthorized"));
        var egress = Build(transport, store, new ProcessSingleWriterLease(), readThrough: false);

        var result = await egress.CallAsync(ChatCaller(), Alice(), Target, "portal_list_items", null, confirmed: false);

        Assert.Equal(401, result.HttpStatus);
        Assert.Equal(1, transport.Calls); // default behavior: no self-heal
    }

    private sealed class SequenceTransport(params TransportResponse[] responses) : IHttpTransport
    {
        private readonly Queue<TransportResponse> _responses = new(responses);
        public int Calls { get; private set; }

        public Task<TransportResponse> SendAsync(string method, string url, IReadOnlyDictionary<string, string> headers, string? body, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(_responses.Count > 0
                ? _responses.Dequeue()
                : new TransportResponse(599, new Dictionary<string, string>(), ""));
        }
    }

    private sealed class DenyingLease : ISingleWriterLease
    {
        public Task<IWriterLeaseHold?> TryAcquireAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IWriterLeaseHold?>(null);
    }

    private sealed class WritableMemoryStore : ICredentialStore, ICredentialWriter
    {
        private readonly Dictionary<string, CredentialBundle> _bundles = new(StringComparer.Ordinal);

        public string Kind => "memory";

        public void Seed(string name, CredentialBundle bundle) => _bundles[name] = bundle;
        public CredentialBundle? Get(string name) => _bundles.TryGetValue(name, out var b) ? b : null;

        public Task<CredentialBundle> GetBundleAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(_bundles.TryGetValue(name, out var b) ? b : CredentialBundle.Empty);

        public Task PutBundleAsync(string name, CredentialBundle bundle, CancellationToken cancellationToken = default)
        {
            _bundles[name] = bundle;
            return Task.CompletedTask;
        }
    }
}
