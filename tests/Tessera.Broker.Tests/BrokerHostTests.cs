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
        var webRoot = Directory.CreateDirectory(Path.Combine(_dir, "web")).FullName;
        File.WriteAllText(Path.Combine(webRoot, "index.html"), "<!doctype html><title>Tessera Test</title>");
        var apiCollisionRoot = Directory.CreateDirectory(Path.Combine(webRoot, "api", "v1")).FullName;
        File.WriteAllText(Path.Combine(apiCollisionRoot, "static-collision.json"), "{\"leaked\":true}");

        var configPath = Path.Combine(_dir, "tessera.json");
        File.WriteAllText(configPath, $$"""
            {
              "server": { "host": "127.0.0.1", "port": {{port}} },
              "serverIdentity": { "id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "displayName": "Tessera Test" },
              "identity": { "mode": "mtls", "trustDomain": "tessera.local" },
              "policy": { "default": "deny" },
              "portal": { "webRoot": {{JsonSerializer.Serialize(webRoot)}} },
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
        public async Task Incomplete_delegated_oidc_identity_lane_fails_closed()
        {
                var port = FreePort();
                var path = Path.Combine(_dir, "incomplete-delegated-oidc.json");
                File.WriteAllText(path, $$"""
                        {
                            "server": { "host": "127.0.0.1", "port": {{port}} },
                            "serverIdentity": { "id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "displayName": "Tessera Test" },
                            "identity": {
                                "mode": "oidc",
                                "trustDomain": "tessera.local",
                                "oidc": {
                                    "issuer": "https://auth.example/application/o/tessera/",
                                    "audience": "tessera-app",
                                    "tenantId": "authentik:tessera"
                                }
                            },
                            "policy": { "default": "deny" },
                            "audit": { "enabled": false }
                        }
                        """);
                await using var app = await BrokerHost.BuildAppAsync(new BrokerHostOptions
                {
                        ConfigPath = path,
                        PolicyPath = Path.Combine(_dir, "grants.json"),
                        StoreOverride = new InMemoryCredentialStore(),
                        ProductDatabasePath = Path.Combine(_dir, "incomplete-delegated-oidc.db"),
                        PluginRoot = Path.Combine(_dir, "no-plugins"),
                        Environment = new Dictionary<string, string?>(StringComparer.Ordinal)
                        {
                                ["TESSERA_DELEGATED_OIDC_ISSUER"] = "https://auth.example/application/o/librechat/",
                                ["TESSERA_DELEGATED_OIDC_AUDIENCE"] = "tessera-app",
                                ["TESSERA_DELEGATED_OIDC_SUBJECT_CLAIM"] = "tessera_subject",
                        },
                });
                await app.StartAsync();
                using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

                using var response = await client.GetAsync(new Uri("/status", UriKind.Relative));
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.StartsWith("fail-closed", document.RootElement.GetProperty("delegation").GetString());
                await app.StopAsync();
        }

    [Fact]
    public async Task Unknown_api_routes_return_problem_details_instead_of_the_spa()
    {
        var response = await _client.GetAsync(new Uri("/api/v1/static-collision.json", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(7, root.EnumerateObject().Count());
        Assert.Equal("https://tessera.local/problems/api-route-not-found", root.GetProperty("type").GetString());
        Assert.Equal("API route not found", root.GetProperty("title").GetString());
        Assert.Equal(404, root.GetProperty("status").GetInt32());
        Assert.Equal("This Tessera server does not expose the requested API route.", root.GetProperty("detail").GetString());
        Assert.Equal("/api/v1/static-collision.json", root.GetProperty("instance").GetString());
        Assert.DoesNotContain("leaked", root.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("api_route_not_found", root.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));
        Assert.DoesNotContain("Tessera Test", root.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_only_servers_return_problem_details_for_unknown_api_routes()
    {
        var port = FreePort();
        var path = Path.Combine(_dir, "api-only.json");
        File.WriteAllText(path, $$"""
            {
              "server": { "host": "127.0.0.1", "port": {{port}} },
              "serverIdentity": { "id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "displayName": "Tessera Test" },
              "identity": { "mode": "dev", "trustDomain": "tessera.local" },
              "policy": { "default": "deny" },
              "audit": { "enabled": false }
            }
            """);
        await using var app = await BrokerHost.BuildAppAsync(new BrokerHostOptions
        {
            ConfigPath = path,
            PolicyPath = Path.Combine(_dir, "grants.json"),
            StoreOverride = new InMemoryCredentialStore(),
            ProductDatabasePath = Path.Combine(_dir, "api-only.db"),
            PluginRoot = Path.Combine(_dir, "no-plugins"),
        });
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var response = await client.GetAsync(new Uri("/api/v1/not-present", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(7, document.RootElement.EnumerateObject().Count());
        Assert.Equal("api_route_not_found", document.RootElement.GetProperty("code").GetString());
        await app.StopAsync();
    }

    [Fact]
    public async Task Product_api_routes_keep_precedence_when_the_spa_is_enabled()
    {
        var port = FreePort();
        var path = Path.Combine(_dir, "product-configured.json");
        var webRoot = Path.Combine(_dir, "web");
        File.WriteAllText(path, $$"""
            {
              "server": { "host": "127.0.0.1", "port": {{port}} },
              "serverIdentity": { "id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "displayName": "Tessera Test" },
              "identity": { "mode": "dev", "trustDomain": "tessera.local" },
              "policy": { "default": "deny" },
              "portal": { "webRoot": {{JsonSerializer.Serialize(webRoot)}} },
              "audit": { "enabled": false }
            }
            """);
        await using var app = await BrokerHost.BuildAppAsync(new BrokerHostOptions
        {
            ConfigPath = path,
            PolicyPath = Path.Combine(_dir, "grants.json"),
            StoreOverride = new InMemoryCredentialStore(),
            ProductDatabasePath = Path.Combine(_dir, "product-precedence.db"),
            PluginRoot = Path.Combine(_dir, "no-plugins"),
        });
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var response = await client.GetAsync(new Uri("/api/v1/conversations", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("Tessera Test", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await app.StopAsync();
    }

    [Fact]
    public async Task Unsupported_api_methods_are_structured_and_never_the_spa()
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/conversations");
        var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("GET", response.Content.Headers.Allow);
        Assert.Contains("POST", response.Content.Headers.Allow);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("https://tessera.local/problems/method-not-allowed", document.RootElement.GetProperty("type").GetString());
        Assert.Equal("method_not_allowed", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Client_side_routes_still_fall_back_to_the_spa()
    {
        var response = await _client.GetAsync(new Uri("/accounts", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Tessera Test", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Server_descriptor_is_public_bounded_and_cache_disabled()
    {
        var response = await _client.GetAsync(new Uri("/.well-known/tessera", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadAsStringAsync();
        Assert.InRange(body.Length, 1, 4096);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal("tessera", root.GetProperty("product").GetString());
        Assert.Equal("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", root.GetProperty("serverId").GetString());
        Assert.Equal("Tessera Test", root.GetProperty("displayName").GetString());
        Assert.Equal("v1", root.GetProperty("apiVersion").GetString());
        Assert.Equal(1, root.GetProperty("protocolVersion").GetInt32());
        Assert.Matches("^[0-9]+\\.[0-9]+\\.[0-9]+$", root.GetProperty("serverVersion").GetString()!);
        Assert.Equal(6, root.EnumerateObject().Count());
    }

    [Fact]
    public async Task Server_descriptor_fails_closed_when_identity_is_unconfigured()
    {
        var port = FreePort();
        var path = Path.Combine(_dir, "unconfigured.json");
        File.WriteAllText(path, $$"""
            {
              "server": { "host": "127.0.0.1", "port": {{port}} },
              "identity": { "mode": "mtls" },
              "policy": { "default": "deny" },
              "audit": { "enabled": false }
            }
            """);
        await using var app = await BrokerHost.BuildAppAsync(new BrokerHostOptions
        {
            ConfigPath = path,
            PolicyPath = Path.Combine(_dir, "missing-grants.json"),
            StoreOverride = new InMemoryCredentialStore(),
        });
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var response = await client.GetAsync(new Uri("/.well-known/tessera", UriKind.Relative));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("server_identity_unconfigured", document.RootElement.GetProperty("code").GetString());
        await app.StopAsync();
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
