using System.Security.Cryptography;
using System.Text.Json;
using Tessera.Core.Configuration;
using Tessera.Core.Kernel;
using Tessera.Core.Stores;
using Tessera.Persistence.Sqlite;
using Tessera.Plugin.Abstractions;
using Tessera.Providers;
using Tessera.Providers.R2;
using Xunit;

namespace Tessera.Broker.Tests;

public sealed class SetupAndCatalogTests
{
    [Fact]
    public async Task Model_bootstrap_is_idempotent_and_sets_canonical_defaults()
    {
        var directory = Directory.CreateTempSubdirectory("tessera-setup-test");
        try
        {
            var store = new SqliteKernelStore(Path.Combine(directory.FullName, "product.db"));
            await store.InitializeAsync();
            var principal = PrincipalRef.Create(
                "https://issuer.example",
                "tenant",
                "subject",
                "owner@example.com",
                DateTimeOffset.UtcNow);
            await store.AddAsync(principal);
            var custody = new InMemoryCredentialStore();
            var config = new TesseraConfig
            {
                ModelGateways = new ModelGatewayOptions
                {
                    Enabled = true,
                    Endpoints = [new ModelGatewayEndpointOptions
                    {
                        Id = "homelab",
                        DisplayName = "Homelab LiteLLM",
                        Endpoint = "http://litellm.test/v1",
                        AutoConnect = true,
                        DefaultModel = "claude-haiku-4.5",
                        DefaultContextLimit = 200_000,
                        CredentialEnvironmentVariable = "TESSERA_LITELLM_MASTER_KEY",
                    }],
                    AllowPlainHttp = true,
                },
            };
            var service = new ModelGatewayBootstrapService(
                config,
                store,
                custody,
                new DelegateTransport((method, url) =>
                    new(
                        200,
                        new Dictionary<string, string>(),
                        "{\"data\":[{\"id\":\"claude-haiku-4.5\"}]}")),
                name => name == "TESSERA_LITELLM_MASTER_KEY" ? "server-owned-secret" : null);

            Assert.Equal("READY_TO_CONNECT", (await service.GetStateAsync(principal.PrincipalId, default)).State);
            var first = await service.BootstrapAsync(principal.PrincipalId, default);
            var second = await service.BootstrapAsync(principal.PrincipalId, default);

            Assert.Equal("CONNECTED", first.State);
            Assert.Equal(first.ProfileId, second.ProfileId);
            var accounts = await store.ListConnectedAccountsAsync(principal.PrincipalId);
            var profiles = await store.ListModelProfilesAsync(principal.PrincipalId);
            Assert.Single(accounts);
            Assert.Single(profiles);
            Assert.Equal("claude-haiku-4.5", profiles[0].Model);
            var settings = await store.GetSettingsAsync(principal.PrincipalId);
            Assert.Equal(profiles[0].ProfileId, settings.DefaultChatModelProfileId);
            Assert.Equal(profiles[0].ProfileId, settings.DefaultLightweightModelProfileId);
            Assert.DoesNotContain("server-owned-secret", accounts[0].NonSecretConfigJson, StringComparison.Ordinal);
            Assert.NotNull(await custody.GetBundleAsync(accounts[0].CredentialRef));
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task Official_registry_search_is_normalized_cached_and_never_installable()
    {
        var calls = 0;
        var response = """
            {
              "servers": [
                {
                  "server": {
                    "name": "io.example/gmail-safe",
                    "title": "Gmail Safe",
                    "description": "Read Gmail metadata through a public MCP server.",
                    "version": "1.2.3",
                    "repository": { "url": "https://github.com/example/gmail-safe", "source": "github" },
                    "packages": [
                      {
                        "registryType": "npm",
                        "identifier": "@example/gmail-safe",
                        "version": "1.2.3",
                        "transport": { "type": "stdio" },
                        "environmentVariables": [
                          { "name": "GMAIL_TOKEN", "isSecret": true }
                        ]
                      }
                    ]
                  },
                  "_meta": {
                    "io.modelcontextprotocol.registry/official": { "status": "active" }
                  }
                }
              ],
              "metadata": { "count": 1 }
            }
            """;
        var source = new McpRegistryCatalogSource(
            new DelegateTransport((method, url) =>
            {
                calls++;
                Assert.Equal("GET", method);
                Assert.Contains("search=gmail", url, StringComparison.Ordinal);
                return new(200, new Dictionary<string, string>(), response);
            }));

        var first = await source.SearchAsync("gmail", 10, new HashSet<string>(), default);
        var second = await source.SearchAsync("gmail", 10, new HashSet<string>(), default);

        var item = Assert.Single(first);
        Assert.Equal("Gmail Safe", item.Name);
        Assert.Equal("VERIFIED_METADATA", item.TrustLevel);
        Assert.Equal("PERSONAL_DATA", item.Sensitivity);
        Assert.Equal("SERVER_REVIEW_REQUIRED", item.InstallationMode);
        Assert.Equal("REVIEW_REQUIRED", item.InstallState);
        Assert.False(item.Installed);
        Assert.Contains("External credentials", item.AuthTypes);
        Assert.Equal(first, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Local_catalog_marks_hash_validated_package_installed()
    {
        var directory = Directory.CreateTempSubdirectory("tessera-catalog-test");
        try
        {
            var packageDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "regina-maria"));
            var manifest = """
                {
                                    "Id": "regina-maria",
                                    "Version": "1.0.0",
                                    "Name": "Regina Maria",
                                    "Publisher": "Tessera",
                                    "MinimumTesseraVersion": "2.0.0",
                                    "Capabilities": [
                    {
                                            "Id": "reginamaria.appointments.list",
                                            "Version": "1",
                                            "Description": "List medical appointments",
                                            "ExecutorKind": "mcp",
                                            "AccountRequired": true,
                                            "RequiredPermissions": ["appointments:read"],
                                            "SideEffectClass": "ReadOnly",
                                            "TimeoutMilliseconds": 10000,
                                            "MaxResultBytes": 65536
                    }
                  ]
                }
                """;
            var manifestPath = Path.Combine(packageDirectory.FullName, "manifest.json");
            await File.WriteAllTextAsync(manifestPath, manifest);
            var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(manifestPath)));
            var catalogPath = Path.Combine(directory.FullName, "catalog.json");
            await File.WriteAllTextAsync(
                catalogPath,
                JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["regina-maria@1.0.0"] = hash,
                }));
            var source = new LocalIntegrationCatalogSource(
                new R2PluginCatalog(directory.FullName, catalogPath));

            var results = await source.SearchAsync(
                "regina maria",
                10,
                new HashSet<string>(["regina-maria"], StringComparer.Ordinal),
                default);

            var item = Assert.Single(results);
            Assert.True(item.Installed);
            Assert.Equal("INSTALLED", item.InstallState);
            Assert.Equal("MCP", item.Runtime);
            Assert.Equal("HEALTH_DATA", item.Sensitivity);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task Plugin_owned_search_is_normalized_cached_and_never_installable()
    {
        var plugin = new FakeCatalogPlugin();
        var source = new PluginIntegrationCatalogSource(
            plugin,
            new DelegateTransport((_, _) => throw new InvalidOperationException("Plugin controls transport use.")));

        var first = await source.SearchAsync("home assistant", 5, new HashSet<string>(), default);
        var second = await source.SearchAsync("home assistant", 5, new HashSet<string>(), default);

        var item = Assert.Single(first);
        Assert.Equal("code-host:homeassistant-ai/ha-mcp", item.Id);
        Assert.Equal("code-host", item.Source);
        Assert.Equal("UNTRUSTED", item.TrustLevel);
        Assert.Equal("SERVER_REVIEW_REQUIRED", item.InstallationMode);
        Assert.Equal("REVIEW_REQUIRED", item.InstallState);
        Assert.False(item.Installed);
        Assert.Equal("MIT", item.License);
        Assert.Equal(first, second);
        Assert.Equal(1, plugin.Calls);
    }

    private sealed class FakeCatalogPlugin : ITesseraCatalogPlugin
    {
        public int Calls { get; private set; }

        public PluginCatalogSourceDescriptor CatalogSource { get; } = new(
            "code-host",
            "Public code repositories",
            TimeSpan.FromHours(1));

        public Task<IReadOnlyList<PluginCatalogItem>> SearchCatalogAsync(
            string query,
            int limit,
            IHttpTransport transport,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Assert.Equal("home assistant", query);
            Assert.Equal(5, limit);
            IReadOnlyList<PluginCatalogItem> items =
            [
                new(
                    "code-host:homeassistant-ai/ha-mcp",
                    "ha mcp",
                    "Home Assistant MCP server",
                    "homeassistant-ai",
                    "MCP candidate",
                    "https://code.example/homeassistant-ai/ha-mcp",
                    "main",
                    "MIT",
                    "UNTRUSTED",
                    ["Home Assistant MCP server"],
                    [],
                    "SERVER_REVIEW_REQUIRED",
                    "REVIEW_REQUIRED",
                    false,
                    "https://code.example/homeassistant-ai/ha-mcp")
            ];
            return Task.FromResult(items);
        }
    }

    private sealed class DelegateTransport(
        Func<string, string, TransportResponse> response) : IHttpTransport
    {
        public Task<TransportResponse> SendAsync(
            string method,
            string url,
            IReadOnlyDictionary<string, string> headers,
            string? body,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response(method, url));
        }
    }
}
