using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Tessera.Core.Egress;
using Tessera.Core.OAuthMcp;
using Tessera.Core.Stores;
using Tessera.Providers;
using Tessera.Providers.OAuthMcp;
using Xunit;

namespace Tessera.Broker.Tests;

/// <summary>
/// Endpoint-level tests for the OAuth-MCP connect surface (ADR 0027, spec W2b):
/// <c>POST /oauth/mcp/connect</c> (operator-authenticated begin) and
/// <c>GET /oauth/mcp/callback</c> (public AS redirect landing). Tier 1 drives the real host to
/// prove the auth/wiring gates (disabled ⇒ 404, unauthenticated ⇒ 401, non-oauth target ⇒ 400,
/// cross-principal ⇒ 403, bad callback ⇒ 400). Tier 2 injects a stub AS (discovery + token) to
/// prove the full begin→callback→binding path writes the per-principal credential end to end.
/// </summary>
public sealed class OAuthMcpConnectEndpointTests
{
    private const string UserAlice = "user-alice-token";
    private const string UserBob = "user-bob-token";
    private const string AdminBoss = "user-boss-token";


    // ── Tier 1: auth + wiring gates (default services) ──────────────────────────

    [Fact]
    public async Task Connect_is_404_when_oauthmcp_disabled()
    {
        await using var h = await Host.BuildAsync(enabled: false);
        var resp = await h.PostConnect(UserAlice, "{\"target\":\"mobbin\"}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Connect_unauthenticated_is_401()
    {
        await using var h = await Host.BuildAsync();
        var resp = await h.PostConnect(caller: null, "{\"target\":\"mobbin\"}");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Connect_to_a_non_oauth_target_is_400()
    {
        await using var h = await Host.BuildAsync();
        var resp = await h.PostConnect(UserAlice, "{\"target\":\"apple-caldav\"}");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Connect_for_another_person_as_non_admin_is_403()
    {
        await using var h = await Host.BuildAsync();
        var resp = await h.PostConnect(UserAlice, "{\"target\":\"mobbin\",\"principal\":\"bob@example.com\"}");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Callback_missing_code_or_state_is_400()
    {
        await using var h = await Host.BuildAsync();
        var resp = await h.Client.GetAsync("/oauth/mcp/callback");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Callback_with_an_unknown_state_is_400()
    {
        await using var h = await Host.BuildAsync();
        var resp = await h.Client.GetAsync("/oauth/mcp/callback?state=never-issued&code=xyz");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── Tier 2: the full begin→callback→binding path (stub AS) ───────────────────

    [Fact]
    public async Task Begin_then_callback_acquires_and_binds_the_per_user_credential()
    {
        var store = new InMemoryCredentialStore();
        var discovery = new OAuthMcpDiscovery(new HttpClient(new StubDiscoveryHandler()));
        var acquirer = new OAuthMcpAcquirer(
            new StubTransport("{\"access_token\":\"AT-alice\",\"refresh_token\":\"RT-alice\"}"),
            store, new SsrfGuard(["as.test"]));
        var connect = new OAuthMcpConnectService(new InMemoryPendingAuthorizationStore(), acquirer);

        await using var h = await Host.BuildAsync(store: store, discovery: discovery, connect: connect);

        // Begin: an authenticated operator connects their own mobbin — returns the authorize URL + state.
        var begin = await h.PostConnect(UserAlice, "{\"target\":\"mobbin\"}");
        Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        using var beginBody = JsonDocument.Parse(await begin.Content.ReadAsStringAsync());
        var authorizeUrl = beginBody.RootElement.GetProperty("authorizeUrl").GetString()!;
        var state = beginBody.RootElement.GetProperty("state").GetString()!;
        Assert.Contains("https://as.test/oauth/authorize?", authorizeUrl, StringComparison.Ordinal);
        Assert.Contains("code_challenge_method=S256", authorizeUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("code_verifier", authorizeUrl, StringComparison.Ordinal);

        // Callback: the AS redirect lands with that state + a code — the credential is acquired.
        var callback = await h.Client.GetAsync($"/oauth/mcp/callback?state={Uri.EscapeDataString(state)}&code=auth-code");
        Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
        Assert.Contains("Connected", await callback.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The per-principal binding now resolves to a live credential (alice sees her own connection).
        var connections = await h.GetConnections(UserAlice);
        Assert.Equal(HttpStatusCode.OK, connections.StatusCode);
        using var list = JsonDocument.Parse(await connections.Content.ReadAsStringAsync());
        var mobbin = list.RootElement.EnumerateArray()
            .FirstOrDefault(e => e.GetProperty("provider").GetString() == "mobbin");
        Assert.Equal(JsonValueKind.Object, mobbin.ValueKind);            // the binding was created
        Assert.True(mobbin.GetProperty("hasAccessToken").GetBoolean());  // and the token was written to the store
    }

    [Fact]
    public async Task Callback_with_an_unknown_state_creates_no_binding()
    {
        var store = new InMemoryCredentialStore();
        var discovery = new OAuthMcpDiscovery(new HttpClient(new StubDiscoveryHandler()));
        var acquirer = new OAuthMcpAcquirer(
            new StubTransport("{\"access_token\":\"AT\"}"), store, new SsrfGuard(["as.test"]));
        var connect = new OAuthMcpConnectService(new InMemoryPendingAuthorizationStore(), acquirer);
        await using var h = await Host.BuildAsync(store: store, discovery: discovery, connect: connect);

        var resp = await h.Client.GetAsync("/oauth/mcp/callback?state=forged&code=xyz");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var connections = await h.GetConnections(UserAlice);
        using var list = JsonDocument.Parse(await connections.Content.ReadAsStringAsync());
        Assert.Empty(list.RootElement.EnumerateArray());
    }

    // ── Test host ────────────────────────────────────────────────────────────────

    private sealed class Host : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Client { get; init; }
        public required string Dir { get; init; }

        public static async Task<Host> BuildAsync(
            bool enabled = true,
            InMemoryCredentialStore? store = null,
            OAuthMcpDiscovery? discovery = null,
            OAuthMcpConnectService? connect = null)
        {
            var port = FreePort();
            var dir = Directory.CreateTempSubdirectory("tessera-oauth-connect").FullName;

            var oauthLine = enabled
                ? "\"oauthMcp\": { \"enabled\": true, \"redirectUri\": \"https://tessera.test/oauth/mcp/callback\", \"clientId\": \"tessera\" }"
                : "\"oauthMcp\": { \"enabled\": false }";
            File.WriteAllText(Path.Combine(dir, "tessera.json"), $$"""
                {
                  "server": { "host": "127.0.0.1", "port": {{port}} },
                  "identity": { "mode": "oidc", "oidc": { "issuer": "https://issuer.example/v2.0", "audience": "tessera" } },
                  "policy": { "default": "deny" },
                  "audit": { "enabled": false },
                  "egress": { "enabled": true, "allowedHosts": ["mob.test", "as.test"] },
                  "portal": { "admins": ["boss@example.com"] },
                  {{oauthLine}}
                }
                """);

            var grantsPath = Path.Combine(dir, "grants.json");
            File.WriteAllText(grantsPath, """
                {
                  "grants": [],
                  "bindings": [],
                  "recipes": [
                    { "target": "mobbin", "egress": "proxy", "injection": "bearer",
                      "oauthMcp": { "mcpUrl": "https://mob.test/mcp", "scopes": ["screens.read"] } },
                    { "target": "apple-caldav", "egress": "proxy", "injection": "basic" }
                  ]
                }
                """);

            var validator = new FakeTokenValidator()
                .AddUser(UserAlice, "alice-oid", "alice@example.com")
                .AddUser(UserBob, "bob-oid", "bob@example.com")
                .AddUser(AdminBoss, "boss-oid", "boss@example.com");

            var options = new BrokerHostOptions
            {
                ConfigPath = Path.Combine(dir, "tessera.json"),
                PolicyPath = grantsPath,
                StoreOverride = store ?? new InMemoryCredentialStore(),
                ValidatorOverride = validator,
                OAuthMcpDiscoveryOverride = discovery,
                OAuthMcpConnectServiceOverride = connect,
            };

            var app = await BrokerHost.BuildAppAsync(options);
            await app.StartAsync();
            return new Host
            {
                App = app,
                Client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") },
                Dir = dir,
            };
        }

        public Task<HttpResponseMessage> PostConnect(string? caller, string json)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/oauth/mcp/connect")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            if (caller is not null)
            {
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {caller}");
            }

            return Client.SendAsync(req);
        }

        public Task<HttpResponseMessage> GetConnections(string caller)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, "/portal/connections");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {caller}");
            return Client.SendAsync(req);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
            try { Directory.Delete(Dir, recursive: true); } catch (IOException) { /* best effort */ }
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }

    // A stub upstream that answers the RFC 9728 probe + serves the RFC 8414 metadata offline.
    private sealed class StubDiscoveryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (request.Method == HttpMethod.Post && url == "https://mob.test/mcp")
            {
                var res = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                res.Headers.TryAddWithoutValidation(
                    "WWW-Authenticate",
                    "Bearer resource_metadata=\"https://mob.test/.well-known/oauth-protected-resource\"");
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

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    // A stub token endpoint: returns the same token JSON for any request (the acquirer's SSRF guard
    // still runs first — this only stands in for the network call).
    private sealed class StubTransport : IHttpTransport
    {
        private readonly string _body;
        public StubTransport(string body) => _body = body;

        public Task<TransportResponse> SendAsync(
            string method, string url, IReadOnlyDictionary<string, string> headers, string? body, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TransportResponse(200, new Dictionary<string, string>(), _body));
    }
}
