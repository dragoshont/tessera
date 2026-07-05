using Tessera.Core.Egress;
using Tessera.Core.OAuthMcp;
using Tessera.Core.Stores;
using Tessera.Providers.OAuthMcp;
using Xunit;

namespace Tessera.Providers.Tests;

/// <summary>
/// The OAuth-MCP connect service + pending-authorization store (spec W2a): the pure
/// authorization-code state machine (mint PKCE+state → stash → authorize URL; redeem
/// code → acquire), exercised offline against a <see cref="FakeTransport"/> mock AS.
/// </summary>
public sealed class OAuthMcpConnectServiceTests
{
    private static readonly Uri Redirect = new("https://tessera.test/oauth/callback");
    private const string Client = "tessera";
    private const string Principal = "alice@example.com";
    private const string Target = "mobbin";
    private const string Secret = "mobbin-alice";

    private static OAuthMcpEndpoints Endpoints() => new(
        Resource: "https://mob.test/mcp",
        AuthorizationServer: new AuthorizationServerMetadata(
            Issuer: "https://as.test",
            AuthorizationEndpoint: "https://as.test/oauth/authorize",
            TokenEndpoint: "https://as.test/oauth/token",
            ScopesSupported: null,
            CodeChallengeMethodsSupported: ["S256"]),
        Scopes: ["screens.read"]);

    private static (OAuthMcpConnectService Svc, InMemoryPendingAuthorizationStore Store, FakeTransport Transport, CapturingWriter Writer) Build(
        int status = 200,
        string body = "{\"access_token\":\"AT\",\"refresh_token\":\"RT\"}",
        Func<DateTimeOffset>? clock = null,
        TimeSpan? ttl = null)
    {
        var store = new InMemoryPendingAuthorizationStore();
        var transport = new FakeTransport(status, body);
        var writer = new CapturingWriter();
        var acquirer = new OAuthMcpAcquirer(transport, writer, new SsrfGuard(["as.test"]));
        var svc = new OAuthMcpConnectService(store, acquirer, ttl, clock);
        return (svc, store, transport, writer);
    }

    // ── Begin ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Begin_returns_an_authorize_url_carrying_state_challenge_resource_and_redirect()
    {
        var (svc, store, _, _) = Build();

        var start = svc.Begin(Endpoints(), Principal, Target, Secret, Redirect, Client);

        Assert.StartsWith("https://as.test/oauth/authorize?", start.AuthorizeUrl.AbsoluteUri, StringComparison.Ordinal);
        var query = start.AuthorizeUrl.Query;
        Assert.Contains("response_type=code", query, StringComparison.Ordinal);
        Assert.Contains($"state={start.State}", query, StringComparison.Ordinal);      // base64url = url-safe, verbatim
        Assert.Contains("code_challenge_method=S256", query, StringComparison.Ordinal);
        Assert.Contains("code_challenge=", query, StringComparison.Ordinal);
        Assert.DoesNotContain("code_verifier", query, StringComparison.Ordinal);        // verifier never on the front channel
        Assert.Contains("resource=https%3A%2F%2Fmob.test%2Fmcp", query, StringComparison.Ordinal);
        Assert.Contains("redirect_uri=https%3A%2F%2Ftessera.test%2Foauth%2Fcallback", query, StringComparison.Ordinal);
        Assert.NotNull(store.Take(start.State, DateTimeOffset.UtcNow));                  // the exchange was stashed
    }

    [Fact]
    public void Begin_rejects_endpoints_without_a_usable_authorize_endpoint()
    {
        var (svc, _, _, _) = Build();
        var bad = Endpoints() with
        {
            AuthorizationServer = new AuthorizationServerMetadata(
                "https://as.test", null, "https://as.test/oauth/token", null, ["S256"]),
        };
        Assert.Throws<ArgumentException>(() => svc.Begin(bad, Principal, Target, Secret, Redirect, Client));
    }

    // ── Complete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Complete_redeems_the_code_and_writes_the_bundle()
    {
        var (svc, _, transport, writer) = Build();
        var start = svc.Begin(Endpoints(), Principal, Target, Secret, Redirect, Client);

        var result = await svc.CompleteAsync(start.State, "auth-code-xyz");

        Assert.Equal(OAuthAcquireStatus.Acquired, result.Status);
        Assert.Equal("https://as.test/oauth/token", transport.LastUrl);
        Assert.Contains("grant_type=authorization_code", transport.LastBody!, StringComparison.Ordinal);
        Assert.Contains("code=auth-code-xyz", transport.LastBody!, StringComparison.Ordinal);
        Assert.Equal(Secret, writer.LastName);
        Assert.Equal("AT", writer.LastBundle!.AccessToken);
        // The refresh context is stamped so the rotation owner (W1) can keep it warm.
        Assert.Equal("https://as.test/oauth/token", writer.LastBundle!.Extra![OAuthMcpAcquirer.ExtraTokenEndpoint]);
    }

    [Fact]
    public async Task Complete_with_an_unknown_state_fails_without_calling_the_token_endpoint()
    {
        var (svc, _, transport, writer) = Build();

        var result = await svc.CompleteAsync("never-issued", "code");

        Assert.Equal(OAuthAcquireStatus.Error, result.Status);
        Assert.Equal(0, transport.Calls);   // an unknown state never reaches the AS
        Assert.Null(writer.LastBundle);
    }

    [Fact]
    public async Task Complete_is_single_use()
    {
        var (svc, _, _, _) = Build();
        var start = svc.Begin(Endpoints(), Principal, Target, Secret, Redirect, Client);

        var first = await svc.CompleteAsync(start.State, "code");
        var second = await svc.CompleteAsync(start.State, "code");

        Assert.Equal(OAuthAcquireStatus.Acquired, first.Status);
        Assert.Equal(OAuthAcquireStatus.Error, second.Status);   // the state is consumed
    }

    [Fact]
    public async Task Complete_after_ttl_expiry_fails_without_calling_the_token_endpoint()
    {
        var now = new[] { DateTimeOffset.UnixEpoch };
        var (svc, _, transport, _) = Build(clock: () => now[0], ttl: TimeSpan.FromMinutes(5));
        var start = svc.Begin(Endpoints(), Principal, Target, Secret, Redirect, Client);

        now[0] = now[0].AddMinutes(10);   // past the 5-minute TTL
        var result = await svc.CompleteAsync(start.State, "code");

        Assert.Equal(OAuthAcquireStatus.Error, result.Status);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task Complete_with_no_code_fails_without_consuming_the_state()
    {
        var (svc, _, transport, _) = Build();
        var start = svc.Begin(Endpoints(), Principal, Target, Secret, Redirect, Client);

        var empty = await svc.CompleteAsync(start.State, "");
        Assert.Equal(OAuthAcquireStatus.Error, empty.Status);
        Assert.Equal(0, transport.Calls);
        // the state survives a code-less callback so a real redirect can still complete
        var real = await svc.CompleteAsync(start.State, "code");
        Assert.Equal(OAuthAcquireStatus.Acquired, real.Status);
    }

    // ── discovery orchestration (BeginForRecipe) ─────────────────────────────────

    [Fact]
    public async Task BeginForRecipe_discovers_then_returns_an_authorize_url()
    {
        var discovery = new OAuthMcpDiscovery(new HttpClient(new StubDiscoveryHandler()), new SsrfGuard(["mob.test", "as.test"]));
        var (svc, store, _, _) = Build();

        var start = await svc.BeginForRecipeAsync(
            discovery, "https://mob.test/mcp", ["screens.read"], Principal, Target, Secret, Redirect, Client);

        Assert.NotNull(start);
        Assert.StartsWith("https://as.test/oauth/authorize?", start!.AuthorizeUrl.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains($"state={start.State}", start.AuthorizeUrl.Query, StringComparison.Ordinal);
        Assert.NotNull(store.Take(start.State, DateTimeOffset.UtcNow));   // discovery → stashed
    }

    [Fact]
    public async Task BeginForRecipe_returns_null_when_the_target_is_not_an_oauth_mcp()
    {
        // A target that does not answer 401 + resource_metadata is not an OAuth-MCP (fail-safe).
        var discovery = new OAuthMcpDiscovery(new HttpClient(new StubDiscoveryHandler { ProbeStatus = System.Net.HttpStatusCode.OK }), new SsrfGuard(["mob.test", "as.test"]));
        var (svc, _, _, _) = Build();

        var start = await svc.BeginForRecipeAsync(
            discovery, "https://mob.test/mcp", ["screens.read"], Principal, Target, Secret, Redirect, Client);

        Assert.Null(start);
    }

    // ── pending store ────────────────────────────────────────────────────────────

    [Fact]
    public void Pending_store_take_is_single_use()
    {
        var store = new InMemoryPendingAuthorizationStore();
        store.Put("s1", Pending(DateTimeOffset.UtcNow.AddMinutes(5)));

        Assert.NotNull(store.Take("s1", DateTimeOffset.UtcNow));
        Assert.Null(store.Take("s1", DateTimeOffset.UtcNow));   // consumed
    }

    [Fact]
    public void Pending_store_take_returns_null_when_expired()
    {
        var store = new InMemoryPendingAuthorizationStore();
        store.Put("s1", Pending(DateTimeOffset.UnixEpoch));
        Assert.Null(store.Take("s1", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Pending_store_evicts_the_soonest_to_expire_when_full()
    {
        var store = new InMemoryPendingAuthorizationStore(capacity: 2);
        store.Put("a", Pending(DateTimeOffset.UtcNow.AddMinutes(1)));   // soonest ⇒ evicted first
        store.Put("b", Pending(DateTimeOffset.UtcNow.AddMinutes(5)));
        store.Put("c", Pending(DateTimeOffset.UtcNow.AddMinutes(9)));   // pushes past capacity

        Assert.Null(store.Take("a", DateTimeOffset.UtcNow));
        Assert.NotNull(store.Take("b", DateTimeOffset.UtcNow));
        Assert.NotNull(store.Take("c", DateTimeOffset.UtcNow));
    }

    private static PendingAuthorization Pending(DateTimeOffset expires) => new(
        Principal, Target, Secret,
        new Uri("https://as.test/oauth/token"), Redirect, Client, "https://mob.test/mcp", "verifier", expires);

    // A stub upstream that answers the RFC 9728 probe + serves the RFC 8414 metadata, so
    // BeginForRecipe's discovery can run offline.
    private sealed class StubDiscoveryHandler : HttpMessageHandler
    {
        public System.Net.HttpStatusCode ProbeStatus { get; init; } = System.Net.HttpStatusCode.Unauthorized;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (request.Method == HttpMethod.Post && url == "https://mob.test/mcp")
            {
                var res = new HttpResponseMessage(ProbeStatus);
                if (ProbeStatus == System.Net.HttpStatusCode.Unauthorized)
                {
                    res.Headers.TryAddWithoutValidation(
                        "WWW-Authenticate",
                        "Bearer resource_metadata=\"https://mob.test/.well-known/oauth-protected-resource\"");
                }
                return Task.FromResult(res);
            }
            if (url == "https://mob.test/.well-known/oauth-protected-resource")
            {
                return Task.FromResult(Json("{\"resource\":\"https://mob.test/mcp\",\"authorization_servers\":[\"https://as.test\"]}"));
            }
            if (url == "https://as.test/.well-known/oauth-authorization-server")
            {
                return Task.FromResult(Json("{\"issuer\":\"https://as.test\",\"authorization_endpoint\":\"https://as.test/oauth/authorize\",\"token_endpoint\":\"https://as.test/oauth/token\",\"code_challenge_methods_supported\":[\"S256\"]}"));
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(string body) =>
            new(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    }

    private sealed class CapturingWriter : ICredentialWriter
    {
        public string? LastName { get; private set; }
        public CredentialBundle? LastBundle { get; private set; }

        public Task PutBundleAsync(string name, CredentialBundle bundle, CancellationToken cancellationToken = default)
        {
            LastName = name;
            LastBundle = bundle;
            return Task.CompletedTask;
        }
    }
}
