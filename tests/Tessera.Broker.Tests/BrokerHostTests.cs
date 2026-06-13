using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Tessera.Core.Stores;
using Xunit;

namespace Tessera.Broker.Tests;

public sealed class BrokerHostTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _dir = null!;

    public async Task InitializeAsync()
    {
        var port = FreePort();
        _dir = Directory.CreateTempSubdirectory("tessera-broker-test").FullName;

        var configPath = Path.Combine(_dir, "tessera.json");
        File.WriteAllText(configPath, $$"""
            {
              "server": { "host": "127.0.0.1", "port": {{port}} },
              "identity": { "mode": "mtls", "trustDomain": "tessera.local" },
              "policy": { "default": "deny" },
              "audit": { "enabled": false }
            }
            """);

        var grantsPath = Path.Combine(_dir, "grants.json");
        File.WriteAllText(grantsPath, """
            {
              "grants": [
                { "caller": "spiffe://tessera.local/selftest", "onBehalfOf": "alice@example.com",
                  "target": "test-target", "actions": ["read:selftest"] }
              ],
              "bindings": [
                { "target": "test-target", "onBehalfOf": "alice@example.com", "credential": "test-secret" }
              ]
            }
            """);

        var store = new InMemoryCredentialStore();
        store.Put("test-secret", new CredentialBundle(AccessToken: "AT", RefreshToken: "RT"));

        var options = new BrokerHostOptions
        {
            ConfigPath = configPath,
            PolicyPath = grantsPath,
            StoreOverride = store,
            Environment = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["TESSERA_SELFTEST_TARGET"] = "test-target",
                ["TESSERA_SELFTEST_PRINCIPAL"] = "alice@example.com",
            },
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

    [Fact]
    public async Task Healthz_is_ok()
    {
        var response = await _client.GetAsync(new Uri("/healthz", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Readyz_is_ready_after_startup()
    {
        var response = await _client.GetAsync(new Uri("/readyz", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Status_reports_fail_closed_posture_and_selftest()
    {
        using var doc = JsonDocument.Parse(await _client.GetStringAsync(new Uri("/status", UriKind.Relative)));
        var root = doc.RootElement;

        Assert.True(root.GetProperty("ready").GetBoolean());
        Assert.Equal("fail-closed", root.GetProperty("brokerEndpoint").GetString());
        Assert.StartsWith("fail-closed", root.GetProperty("delegation").GetString(), StringComparison.Ordinal);

        // The read-only self-test resolved the seeded credential's STATUS (not bytes).
        var selfTest = root.GetProperty("selfTest");
        Assert.Equal("allow", selfTest.GetProperty("effect").GetString());
        Assert.Equal("present", selfTest.GetProperty("credentialStatus").GetString());
        Assert.True(selfTest.GetProperty("ok").GetBoolean());

        // The audit-safe detail must never contain the secret value.
        Assert.DoesNotContain("AT", selfTest.GetProperty("credentialDetail").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Broker_endpoint_fails_closed_with_503()
    {
        var response = await _client.PostAsync(new Uri("/v1/broker", UriKind.Relative), new StringContent("{}"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
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
