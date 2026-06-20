using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Tessera.Core.Stores;
using Xunit;

namespace Tessera.Broker.Tests;

/// <summary>
/// Endpoint-level tests for the raw egress proxy front door <c>ANY /v1/egress/{target}</c>
/// (ADR 0022), driving the real HTTP surface with a recording forwarder (no socket).
/// They prove the security gates end-to-end: fail-closed (no authenticator / egress
/// off), caller authentication, the SSRF host allow-list (incl. the iCloud partition
/// pattern), the method→action map, the confused-deputy defense (a forwarded user can
/// only reach their own grant), and step-up on writes.
/// </summary>
public sealed class EgressProxyEndpointTests : IAsyncLifetime
{
    private const string CallerApp = "caller-app-token";   // the apple-mcp's own app-only token
    private const string CallerId = "apple-mcp";
    private const string UserAlice = "user-alice-token";   // forwarded end-user token (granted)
    private const string UserBob = "user-bob-token";       // forwarded end-user token (NOT granted)
    private const string DefaultUpstream = "https://caldav.icloud.com/123/calendars/";

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private RecordingForwarder _forwarder = null!;
    private string _dir = null!;

    public async Task InitializeAsync()
    {
        _forwarder = new RecordingForwarder();
        (_app, _client, _dir) = await BuildAppAsync(egressEnabled: true, _forwarder);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    // ── Fail-closed gates ─────────────────────────────────────────────────────

    [Fact]
    public async Task Egress_disabled_returns_503()
    {
        // A separate host with egress OFF: the front door must refuse, not reach upstream.
        var forwarder = new RecordingForwarder();
        var (app, client, dir) = await BuildAppAsync(egressEnabled: false, forwarder);
        try
        {
            var resp = await client.SendAsync(Build("PROPFIND"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
            Assert.False(forwarder.Forwarded);
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public async Task Missing_caller_token_is_401()
    {
        var resp = await _client.SendAsync(Build("PROPFIND", caller: null));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.False(_forwarder.Forwarded);
    }

    [Fact]
    public async Task A_user_token_as_caller_is_401()
    {
        // A human token presented as the caller is rejected (a person is a subject, not a workload).
        var resp = await _client.SendAsync(Build("PROPFIND", caller: UserAlice));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.False(_forwarder.Forwarded);
    }

    [Fact]
    public async Task An_unknown_target_without_a_caller_is_401_not_404()
    {
        // Auth runs before the target lookup, so an unauthenticated caller can't enumerate
        // which proxy targets exist (the 404 is gated behind authentication).
        var resp = await _client.SendAsync(Build("PROPFIND", target: "not-a-target", caller: null));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    // ── Routing / input validation ────────────────────────────────────────────

    [Fact]
    public async Task Unknown_target_is_404()
    {
        var resp = await _client.SendAsync(Build("PROPFIND", target: "not-a-target"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Missing_upstream_header_is_400()
    {
        var resp = await _client.SendAsync(Build("PROPFIND", upstream: null));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── SSRF host allow-list ──────────────────────────────────────────────────

    [Fact]
    public async Task Upstream_host_not_allow_listed_is_403()
    {
        var resp = await _client.SendAsync(Build("PROPFIND", upstream: "https://evil.example.com/"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.False(_forwarder.Forwarded);
    }

    [Fact]
    public async Task Partition_redirect_host_from_pattern_is_allowed()
    {
        // The RFC 6764 discovery redirect target pNN-caldav.icloud.com is allow-listed by pattern.
        var resp = await _client.SendAsync(Build("PROPFIND", upstream: "https://p52-caldav.icloud.com/123/cal/"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(_forwarder.Forwarded);
    }

    [Fact]
    public async Task A_non_default_upstream_port_is_403()
    {
        // OWASP SSRF: restrict the port, not just the host. An allow-listed host on a
        // non-standard port (e.g. a blind port-probe) is refused.
        var resp = await _client.SendAsync(Build("PROPFIND", upstream: "https://caldav.icloud.com:22/"));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.False(_forwarder.Forwarded);
    }

    // ── Method → action map ───────────────────────────────────────────────────

    [Fact]
    public async Task A_disallowed_method_is_405()
    {
        var resp = await _client.SendAsync(Build("TRACE"));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
        Assert.False(_forwarder.Forwarded);
    }

    // ── Authorization (read allowed; confused-deputy denied) ──────────────────

    [Fact]
    public async Task A_granted_read_is_forwarded()
    {
        var resp = await _client.SendAsync(Build("PROPFIND")); // alice, read:dav granted
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(_forwarder.Forwarded);
    }

    [Fact]
    public async Task A_forwarded_user_can_only_reach_their_own_grant()
    {
        // Confused-deputy (F2): the caller forwards Bob's verified token; Bob has no grant,
        // so the read is denied — the caller can't address another user by swapping the token.
        var resp = await _client.SendAsync(Build("PROPFIND", obo: UserBob));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.False(_forwarder.Forwarded);
    }

    [Fact]
    public async Task A_mode_p_request_with_no_forwarded_user_is_denied()
    {
        // The grant is delegated (onBehalfOf=alice); a request with no forwarded user
        // matches no grant → denied. The proxy can't act as the bare app here.
        var resp = await _client.SendAsync(Build("PROPFIND", obo: null));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.False(_forwarder.Forwarded);
    }

    // ── Step-up on writes (the manage plane) ──────────────────────────────────

    [Fact]
    public async Task A_write_without_confirmation_is_409_step_up()
    {
        var resp = await _client.SendAsync(Build("DELETE")); // manage:dav → step-up
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.False(_forwarder.Forwarded);
    }

    [Fact]
    public async Task A_write_with_confirmation_is_forwarded()
    {
        var resp = await _client.SendAsync(Build("DELETE", confirm: true));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(_forwarder.Forwarded);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HttpRequestMessage Build(
        string method,
        string target = "apple-caldav",
        string? caller = CallerApp,
        string? obo = UserAlice,
        string? upstream = DefaultUpstream,
        bool confirm = false)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), $"/v1/egress/{target}");
        if (caller is not null)
        {
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {caller}");
        }

        if (obo is not null)
        {
            req.Headers.TryAddWithoutValidation("X-Tessera-On-Behalf-Of", obo);
        }

        if (upstream is not null)
        {
            req.Headers.TryAddWithoutValidation("X-Tessera-Upstream", upstream);
        }

        if (confirm)
        {
            req.Headers.TryAddWithoutValidation("X-Tessera-Confirm", "true");
        }

        return req;
    }

    private static async Task<(WebApplication App, HttpClient Client, string Dir)> BuildAppAsync(
        bool egressEnabled, RecordingForwarder forwarder)
    {
        var port = FreePort();
        var dir = Directory.CreateTempSubdirectory("tessera-egress-proxy").FullName;

        var configPath = Path.Combine(dir, "tessera.json");
        File.WriteAllText(configPath, $$"""
            {
              "server": { "host": "127.0.0.1", "port": {{port}} },
              "identity": { "mode": "oidc", "oidc": { "issuer": "https://issuer.example/v2.0", "audience": "tessera" } },
              "policy": { "default": "deny", "manageRequiresStepUp": true },
              "audit": { "enabled": false },
              "egress": { "enabled": {{(egressEnabled ? "true" : "false")}}, "allowedHosts": ["caldav.icloud.com", "re:^p\\d{1,3}-(caldav|contacts)\\.icloud\\.com$"] }
            }
            """);

        // apple-mcp may read + manage CalDAV on behalf of alice. manage steps up by default.
        var grantsPath = Path.Combine(dir, "grants.json");
        File.WriteAllText(grantsPath, $$"""
            {
              "grants": [
                { "caller": "{{CallerId}}", "onBehalfOf": "alice@example.com",
                  "target": "apple-caldav", "actions": ["read:dav", "manage:dav"] }
              ],
              "bindings": [
                { "target": "apple-caldav", "onBehalfOf": "alice@example.com", "credential": "apple-account-a" }
              ],
              "recipes": [
                { "target": "apple-caldav", "egress": "proxy", "injection": "basic" }
              ]
            }
            """);

        var store = new InMemoryCredentialStore();
        store.Put("apple-account-a", new CredentialBundle(
            AccessToken: "app-specific-pw",
            Extra: new Dictionary<string, string> { ["username"] = "alice@icloud.com" }));

        var validator = new FakeTokenValidator()
            .AddApp(CallerApp, CallerId)
            .AddUser(UserAlice, "alice-oid", "alice@example.com")
            .AddUser(UserBob, "bob-oid", "bob@example.com");

        var options = new BrokerHostOptions
        {
            ConfigPath = configPath,
            PolicyPath = grantsPath,
            StoreOverride = store,
            ValidatorOverride = validator,
            ForwarderOverride = forwarder,
        };

        var app = await BrokerHost.BuildAppAsync(options);
        await app.StartAsync();
        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        return (app, client, dir);
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
