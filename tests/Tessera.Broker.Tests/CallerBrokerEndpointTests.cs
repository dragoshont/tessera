using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Tessera.Core.Stores;
using Xunit;

namespace Tessera.Broker.Tests;

/// <summary>
/// Endpoint-level tests for the caller plane <c>POST /v1/broker</c> (ADR 0021),
/// driving the real HTTP surface: the two fail-closed gates, caller authentication
/// at the wire, op routing (check/list-tools/call/invoke), and the status→HTTP
/// mapping. Complements <see cref="CallerBrokerServiceTests"/> (the logic) by
/// exercising the endpoint wiring (body parse, headers, status codes).
/// </summary>
public sealed class CallerBrokerEndpointTests : IAsyncLifetime
{
    private const string CallerApp = "caller-token";
    private const string CallerId = "media-mcp";
    private const string UserToken = "user-token";

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _dir = null!;

    public async Task InitializeAsync()
    {
        var port = FreePort();
        _dir = Directory.CreateTempSubdirectory("tessera-caller-endpoint").FullName;

        // identity.mode=oidc + issuer passes config validation; the ValidatorOverride
        // (DelegationEnabled=true) opens the caller-auth gate without a live IdP.
        var configPath = Path.Combine(_dir, "tessera.json");
        File.WriteAllText(configPath, $$"""
            {
              "server": { "host": "127.0.0.1", "port": {{port}} },
              "identity": { "mode": "oidc", "oidc": { "issuer": "https://issuer.example/v2.0", "audience": "tessera" } },
              "policy": { "default": "deny" },
              "audit": { "enabled": false },
              "egress": { "enabled": false, "allowedHosts": [] }
            }
            """);

        // A caller-scoped grant (Mode P — no onBehalfOf): the app may read on sonarr.
        var grantsPath = Path.Combine(_dir, "grants.json");
        File.WriteAllText(grantsPath, $$"""
            {
              "grants": [
                { "caller": "{{CallerId}}", "target": "sonarr", "actions": ["read:series"] }
              ],
              "bindings": [
                { "target": "sonarr", "credential": "sonarr-key", "owner": "service" }
              ]
            }
            """);

        var store = new InMemoryCredentialStore();
        store.Put("sonarr-key", new CredentialBundle(AccessToken: "AT"));

        var validator = new FakeTokenValidator()
            .AddApp(CallerApp, CallerId)
            .AddUser(UserToken, "alice-oid", "alice@example.com");

        var options = new BrokerHostOptions
        {
            ConfigPath = configPath,
            PolicyPath = grantsPath,
            StoreOverride = store,
            ValidatorOverride = validator,
        };

        _app = await BrokerHost.BuildAppAsync(options);
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static HttpRequestMessage Post(string body, string? caller = CallerApp, string? onBehalfOf = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/broker")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (caller is not null)
        {
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {caller}");
        }
        if (onBehalfOf is not null)
        {
            req.Headers.TryAddWithoutValidation("X-Tessera-On-Behalf-Of", onBehalfOf);
        }
        return req;
    }

    [Fact]
    public async Task Missing_caller_token_is_401()
    {
        var resp = await _client.SendAsync(Post("""{ "target": "sonarr", "op": "check", "action": "read:series" }""", caller: null));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task A_user_token_as_caller_is_401()
    {
        // A human token presented as the caller must be rejected (a person is a subject, not a workload).
        var resp = await _client.SendAsync(Post("""{ "target": "sonarr", "op": "check", "action": "read:series" }""", caller: UserToken));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Check_a_granted_action_is_200_allow()
    {
        var resp = await _client.SendAsync(Post("""{ "op": "check", "target": "sonarr", "action": "read:series" }"""));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("allow", doc.RootElement.GetProperty("effect").GetString());
    }

    [Fact]
    public async Task Check_an_ungranted_action_is_200_deny()
    {
        var resp = await _client.SendAsync(Post("""{ "op": "check", "target": "sonarr", "action": "manage:settings" }"""));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("deny", doc.RootElement.GetProperty("effect").GetString());
    }

    [Fact]
    public async Task Call_with_egress_disabled_is_403_notallowed()
    {
        // The gateway is disabled (egress off), so a call reaches no upstream → notallowed → 403.
        var resp = await _client.SendAsync(Post("""{ "op": "call", "target": "sonarr", "tool": "sonarr_series" }"""));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("notallowed", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task List_tools_is_200_for_an_authenticated_caller()
    {
        // Egress disabled ⇒ the disabled gateway lists no tools, but the caller is
        // authenticated and the op routes — a 200 with an empty list, not an error.
        var resp = await _client.SendAsync(Post("""{ "op": "list-tools", "target": "sonarr" }"""));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task Invoke_without_method_and_path_is_400()
    {
        var resp = await _client.SendAsync(Post("""{ "op": "invoke", "target": "sonarr" }"""));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task An_unknown_op_is_400()
    {
        var resp = await _client.SendAsync(Post("""{ "op": "frobnicate", "target": "sonarr" }"""));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task A_body_without_target_is_400()
    {
        var resp = await _client.SendAsync(Post("""{ "op": "check", "action": "read:series" }"""));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
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
