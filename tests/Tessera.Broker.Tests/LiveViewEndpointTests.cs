using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Tessera.Core.Portal;
using Tessera.Core.Stores;
using Xunit;

namespace Tessera.Broker.Tests;

/// <summary>
/// End-to-end HTTP tests for the live hand-off endpoint WITH a browser worker wired
/// (ADR 0016 §3 / Job A). Runs the broker in dev mode on loopback with a fake
/// <see cref="ILiveViewProvider"/> injected via <c>LiveViewProviderOverride</c>, so
/// the request → handle path is proven without standing up a real noVNC worker. The
/// fail-closed (no worker) case is covered in <c>PortalEndpointsTests</c>.
/// </summary>
public sealed class LiveViewEndpointTests : IAsyncLifetime
{
    private const string DevHeader = "X-Tessera-Dev-Principal";
    private const string Admin = "alice@example.com";
    private const string Member = "bob@example.com";

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _dir = null!;

    /// <summary>A fake worker that arms a fixed session — proves the endpoint shaping.</summary>
    private sealed class FakeWorkerProvider : ILiveViewProvider
    {
        public Task<LiveViewResult> RequestAsync(string connectionId, string principal, CancellationToken cancellationToken = default) =>
            Task.FromResult(LiveViewResult.Ok(new LiveViewHandle(
                LiveViewUrl: $"https://worker.internal/s/{principal}",
                Mode: LiveViewMode.ReadWrite,
                SessionTtlSeconds: 300,
                ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(300),
                TargetHostname: "portal.example-health.com",
                FaviconUrl: null)));
    }

    public async Task InitializeAsync()
    {
        var port = FreePort();
        _dir = Directory.CreateTempSubdirectory("tessera-liveview-test").FullName;

        var configPath = Path.Combine(_dir, "tessera.json");
        File.WriteAllText(configPath, $$"""
            {
              "server": { "host": "127.0.0.1", "port": {{port}} },
              "identity": { "mode": "dev", "trustDomain": "tessera.local" },
              "policy": { "default": "deny" },
              "audit": { "enabled": false },
              "portal": { "admins": ["alice@example.com"] }
            }
            """);

        var grantsPath = Path.Combine(_dir, "grants.json");
        File.WriteAllText(grantsPath, """
            {
              "grants": [],
              "bindings": [
                { "target": "health-portal", "onBehalfOf": "alice@example.com", "credential": "hp-alice" },
                { "target": "health-portal", "onBehalfOf": "bob@example.com",   "credential": "hp-bob" }
              ],
              "recipes": [ { "target": "health-portal", "egress": "none", "description": "Health Portal" } ]
            }
            """);

        _app = await BrokerHost.BuildAppAsync(new BrokerHostOptions
        {
            ConfigPath = configPath,
            PolicyPath = grantsPath,
            StoreOverride = new InMemoryCredentialStore(),
            LiveViewProviderOverride = new FakeWorkerProvider(),
        });
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private static HttpRequestMessage As(string principal, HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        req.Headers.Add(DevHeader, principal);
        return req;
    }

    [Fact]
    public async Task The_owner_gets_a_live_view_handle_when_a_worker_is_wired()
    {
        var response = await _client.SendAsync(As(Admin, HttpMethod.Post, $"/portal/connections/health-portal:{Admin}/live-view"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("readwrite", doc.RootElement.GetProperty("mode").GetString());
        Assert.Equal("portal.example-health.com", doc.RootElement.GetProperty("targetHostname").GetString());
        Assert.Equal(300, doc.RootElement.GetProperty("sessionTtlSeconds").GetInt32());
        Assert.Contains("worker.internal", doc.RootElement.GetProperty("liveViewUrl").GetString());
    }

    [Fact]
    public async Task A_member_can_seed_their_own_connection()
    {
        var response = await _client.SendAsync(As(Member, HttpMethod.Post, $"/portal/connections/health-portal:{Member}/live-view"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_member_still_cannot_seed_another_persons_connection_even_with_a_worker()
    {
        // The authorization gate runs BEFORE the worker is ever consulted.
        var response = await _client.SendAsync(As(Member, HttpMethod.Post, $"/portal/connections/health-portal:{Admin}/live-view"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Live_view_still_requires_authentication()
    {
        var response = await _client.PostAsync(new Uri($"/portal/connections/health-portal:{Admin}/live-view", UriKind.Relative), content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
