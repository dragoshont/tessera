using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tessera.Core.Configuration;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
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
    public async Task Model_bootstrap_is_concurrency_safe_repairs_custody_and_sets_canonical_defaults()
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
            var config = ModelConfig();
            var probeCalls = 0;
            var service = new ModelGatewayBootstrapService(
                config,
                store,
                custody,
                new DelegateTransport((method, url) =>
                {
                    probeCalls++;
                    return new(
                        200,
                        new Dictionary<string, string>(),
                        "{\"data\":[{\"id\":\"claude-haiku-4.5\"}]}");
                }),
                name => name == "TESSERA_LITELLM_MASTER_KEY" ? "server-owned-secret" : null);

            Assert.Equal("READY_TO_CONNECT", (await service.GetStateAsync(principal.PrincipalId, default)).State);
            var states = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => service.BootstrapAsync(principal.PrincipalId, default)));

            Assert.All(states, state => Assert.Equal("CONNECTED", state.State));
            Assert.Single(states.Select(state => state.ProfileId).Distinct(StringComparer.Ordinal));
            Assert.Equal(1, probeCalls);
            var accounts = await store.ListConnectedAccountsAsync(principal.PrincipalId);
            var profiles = await store.ListModelProfilesAsync(principal.PrincipalId);
            Assert.Single(accounts);
            Assert.Single(profiles);
            Assert.Equal("claude-haiku-4.5", profiles[0].Model);
            var settings = await store.GetSettingsAsync(principal.PrincipalId);
            Assert.Equal(profiles[0].ProfileId, settings.DefaultChatModelProfileId);
            Assert.Equal(profiles[0].ProfileId, settings.DefaultLightweightModelProfileId);
            Assert.DoesNotContain("server-owned-secret", accounts[0].NonSecretConfigJson, StringComparison.Ordinal);
            Assert.False((await custody.GetBundleAsync(accounts[0].CredentialRef)).IsEmpty);

            custody.Put(accounts[0].CredentialRef, CredentialBundle.Empty);
            var stale = await service.GetStateAsync(principal.PrincipalId, default);
            Assert.Equal("READY_TO_CONNECT", stale.State);
            Assert.Equal("model_profile_repair_required", stale.DetailCode);

            var repaired = await service.BootstrapAsync(principal.PrincipalId, default);
            Assert.Equal("CONNECTED", repaired.State);
            Assert.Equal(2, probeCalls);
            Assert.False((await custody.GetBundleAsync(accounts[0].CredentialRef)).IsEmpty);

            var current = await store.GetConnectedAccountAsync(principal.PrincipalId, accounts[0].AccountId)
                ?? throw new InvalidOperationException("Expected model account.");
            await new R2ConnectedAccountService(store, custody)
                .RevokeAsync(principal.PrincipalId, current.AccountId, current.Version);
            var revoked = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.BootstrapAsync(principal.PrincipalId, default));
            Assert.Equal("gateway_binding_conflict", revoked.Message);
            Assert.True((await custody.GetBundleAsync(accounts[0].CredentialRef)).IsEmpty);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task Model_bootstrap_rejects_conflicting_deterministic_account_binding()
    {
        var directory = Directory.CreateTempSubdirectory("tessera-setup-conflict-test");
        try
        {
            var store = new SqliteKernelStore(Path.Combine(directory.FullName, "product.db"));
            await store.InitializeAsync();
            var owner = PrincipalRef.Create(
                "https://issuer.example",
                "tenant",
                "conflict-subject",
                "owner@example.com",
                DateTimeOffset.UtcNow);
            await store.AddAsync(owner);
            var custody = new InMemoryCredentialStore();
            var accountId = StableId(owner.PrincipalId, "model-account", "homelab");
            await new R2ConnectedAccountService(store, custody).ConnectAsync(
                owner.PrincipalId,
                accountId,
                "openai-compatible",
                "model-provider",
                "1.0.0",
                "Conflicting gateway",
                "{}",
                new CredentialBundle(AccessToken: "existing-secret"),
                [],
                [new("model-provider", "1.0.0", "model.chat.complete", "1")]);
            var service = new ModelGatewayBootstrapService(
                ModelConfig(),
                store,
                custody,
                new DelegateTransport((_, _) => new(
                    200,
                    new Dictionary<string, string>(),
                    "{\"data\":[{\"id\":\"claude-haiku-4.5\"}]}")),
                name => name == "TESSERA_LITELLM_MASTER_KEY" ? "server-owned-secret" : null);

            var conflict = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.BootstrapAsync(owner.PrincipalId, default));

            Assert.Equal("gateway_binding_conflict", conflict.Message);
            var account = await store.GetConnectedAccountAsync(owner.PrincipalId, accountId);
            Assert.NotNull(account);
            Assert.Equal(AccountLifecycle.Connecting, account.Lifecycle);
            Assert.Equal("existing-secret", (await custody.GetBundleAsync(account.CredentialRef)).AccessToken);
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
        Assert.Null(item.InspectUrl);
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
                    "BUILT_IN",
                    ["Home Assistant MCP server"],
                    [],
                    "SERVER_INSTALLED",
                    "INSTALLED",
                    true,
                    "http://127.0.0.1/admin")
            ];
            return Task.FromResult(items);
        }
    }

    private static TesseraConfig ModelConfig()
        => new()
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

    private static string StableId(string owner, string kind, string value)
        => Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{owner}\n{kind}\n{value}")));

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
