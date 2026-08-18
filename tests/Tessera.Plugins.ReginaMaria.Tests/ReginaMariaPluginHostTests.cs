using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Tessera.Broker;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Tessera.Core.Stores;
using Tessera.Mcp.Client;
using Tessera.Persistence.Sqlite;
using Tessera.Plugin.Abstractions;
using Tessera.Plugins.ReginaMaria;
using Tessera.Providers;
using Xunit;

namespace Tessera.Plugins.ReginaMaria.Tests;

public sealed class ReginaMariaPluginHostTests
{
    private const string User = "owner@example.com";

    [Fact]
    public async Task Direct_invoke_uses_discovered_module_and_generic_mcp_runtime()
    {
        using var module = ModuleRegistry.Create();
        var runtime = new ListMcpRuntime();
        await using var host = await TestHost.StartAsync(module.Registry, runtime);
        await host.SeedAsync();

        var response = await host.SendAsync(
            "/api/v1/capabilities/reginamaria.appointments.list/invoke",
            new
            {
                capabilityId = "reginamaria.appointments.list",
                capabilityVersion = "1",
                pluginId = "regina-maria",
                pluginVersion = "1.0.0",
                accountId = "rm-owner",
                target = "appointments:list",
                input = new { upcoming = true, maxResults = 20 },
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("appointment-1", body.GetProperty("result").GetProperty("appointments")[0].GetProperty("id").GetString());
        Assert.Equal(["rm_list_appointments"], runtime.Calls);
        Assert.All(runtime.Endpoints, endpoint => Assert.Equal("/mcp/", endpoint.AbsolutePath));
    }

    [Fact]
    public async Task Direct_invoke_denies_RM_output_union_without_a_compatible_branch()
    {
        using var module = ModuleRegistry.Create();
        var runtime = new ListMcpRuntime(incompatibleCreateOutput: true);
        await using var host = await TestHost.StartAsync(module.Registry, runtime);
        await host.SeedAsync();

        var response = await host.SendAsync(
            "/api/v1/capabilities/reginamaria.appointments.list/invoke",
            new
            {
                capabilityId = "reginamaria.appointments.list",
                capabilityVersion = "1",
                pluginId = "regina-maria",
                pluginVersion = "1.0.0",
                accountId = "rm-owner",
                target = "appointments:list",
                input = new { upcoming = true, maxResults = 20 },
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("mcp_schema_incompatible", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(runtime.Calls);
    }

    [Fact]
    public async Task Direct_invoke_invalid_request_reports_only_a_safe_stage()
    {
        using var module = ModuleRegistry.Create();
        var runtime = new ListMcpRuntime();
        await using var host = await TestHost.StartAsync(module.Registry, runtime);
        await host.SeedAsync();
        object input = "leaf";
        for (var depth = 0; depth < 14; depth++)
            input = new Dictionary<string, object?> { ["nested"] = input };

        var response = await host.SendAsync(
            "/api/v1/capabilities/reginamaria.appointments.list/invoke",
            new
            {
                capabilityId = "reginamaria.appointments.list",
                capabilityVersion = "1",
                pluginId = "regina-maria",
                pluginVersion = "1.0.0",
                accountId = "rm-owner",
                target = "appointments:list",
                input,
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("invalid_request", problem.GetProperty("code").GetString());
        Assert.Equal("execution", problem.GetProperty("stage").GetString());
        Assert.False(problem.TryGetProperty("detail", out _));
        Assert.Empty(runtime.Calls);
    }

    [Fact]
    public async Task Direct_invoke_unbound_request_reports_the_request_stage()
    {
        using var module = ModuleRegistry.Create();
        var runtime = new ListMcpRuntime();
        await using var host = await TestHost.StartAsync(module.Registry, runtime);
        await host.SeedAsync();

        var response = await host.SendAsync(
            "/api/v1/capabilities/reginamaria.appointments.list/invoke",
            new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("invalid_request", problem.GetProperty("code").GetString());
        Assert.Equal("request", problem.GetProperty("stage").GetString());
        Assert.False(problem.TryGetProperty("detail", out _));
        Assert.Empty(runtime.Calls);
    }

    [Fact]
    public async Task Direct_invoke_discovery_rejection_reports_registry_capability_stage()
    {
        using var module = ModuleRegistry.Create();
        var runtime = new ListMcpRuntime(rejectDiscovery: true);
        await using var host = await TestHost.StartAsync(module.Registry, runtime);
        await host.SeedAsync();

        var response = await host.SendAsync(
            "/api/v1/capabilities/reginamaria.appointments.list/invoke",
            new
            {
                capabilityId = "reginamaria.appointments.list",
                capabilityVersion = "1",
                pluginId = "regina-maria",
                pluginVersion = "1.0.0",
                accountId = "rm-owner",
                target = "appointments:list",
                input = new { upcoming = true },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("invalid_request", problem.GetProperty("code").GetString());
        Assert.Equal("registry-capability", problem.GetProperty("stage").GetString());
        Assert.False(problem.TryGetProperty("detail", out _));
        Assert.Empty(runtime.Calls);
    }

    [Theory]
    [InlineData("https://rm.example/mcp")]
    [InlineData("https://rm.example/mcp/")]
    [InlineData("https://rm.example/mcp//")]
    public void Canonical_endpoint_has_exact_streamable_http_path(string value)
        => Assert.Equal("https://rm.example/mcp/", ReginaMariaPlugin.CanonicalMcpEndpoint(new Uri(value)).AbsoluteUri);

    [Fact]
    public async Task Host_discovers_hash_catalog_and_joins_declarative_package()
    {
        using var module = ModuleRegistry.Create();
        var runtime = new ListMcpRuntime();
        await using var host = await TestHost.StartFromCatalogAsync(module.Path, module.CatalogPath, runtime);
        await host.SeedAsync();

        var response = await host.SendAsync(
            "/api/v1/capabilities/reginamaria.appointments.list/invoke",
            new
            {
                capabilityId = "reginamaria.appointments.list",
                capabilityVersion = "1",
                pluginId = "regina-maria",
                pluginVersion = "1.0.0",
                accountId = "rm-owner",
                target = "appointments:list",
                input = new { upcoming = true },
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["rm_list_appointments"], runtime.Calls);
    }

    [Fact]
    public async Task Discovered_plugin_owns_connector_and_account_identity_routes()
    {
        using var module = ModuleRegistry.Create();
        var runtime = new ListMcpRuntime();
        await using var host = await TestHost.StartFromCatalogAsync(
            module.Path,
            module.CatalogPath,
            runtime,
            enableReginaMaria: true);

        using var connectorsRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/accounts/regina-maria/connectors");
        connectorsRequest.Headers.Add("X-Tessera-Dev-Principal", User);
        var connectors = await host.Client.SendAsync(connectorsRequest);
        Assert.Equal(HttpStatusCode.OK, connectors.StatusCode);

        var connected = await host.SendAsync(
            "/api/v1/accounts/regina-maria/connect",
            new { connectorId = "account-a", displayName = "My Regina Maria" });
        Assert.Equal(HttpStatusCode.Created, connected.StatusCode);
        var account = JsonDocument.Parse(await connected.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("owner-role", account.GetProperty("providerAccountId").GetString());
        Assert.Equal("CONNECTED", account.GetProperty("lifecycle").GetString());
        Assert.Contains("rm_session_status", runtime.Calls);
        Assert.Contains("rm_account_identity", runtime.Calls);
        Assert.All(runtime.Endpoints, endpoint => Assert.Equal("/mcp/", endpoint.AbsolutePath));
    }

    [Fact]
    public async Task Background_health_canonicalizes_existing_stored_endpoint()
    {
        using var module = ModuleRegistry.Create();
        var runtime = new ListMcpRuntime();
        await using var host = await TestHost.StartAsync(module.Registry, runtime);
        await host.SeedAsync();

        await host.RunReginaMariaHealthPassAsync(runtime);

        Assert.Contains("rm_session_status", runtime.Calls);
        Assert.Contains("rm_account_identity", runtime.Calls);
        Assert.All(runtime.Endpoints, endpoint => Assert.Equal("/mcp/", endpoint.AbsolutePath));
        var account = await host.Store.GetConnectedAccountAsync(TestHost.Owner(), "rm-owner");
        Assert.NotNull(account);
        Assert.Equal(AccountLifecycle.Connected, account.Lifecycle);
        Assert.Equal(AccountHealth.Healthy, account.Health);
    }

    [Fact]
    public async Task Authoritative_missing_module_does_not_prevent_startup_and_denies_capability()
    {
        var missing = PluginModuleDiscovery.Discover(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            []);
        var runtime = new ListMcpRuntime();
        await using var host = await TestHost.StartAsync(missing, runtime);
        await host.SeedAsync();

        var health = await host.Client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var response = await host.SendAsync(
            "/api/v1/capabilities/reginamaria.appointments.list/invoke",
            new
            {
                capabilityId = "reginamaria.appointments.list",
                capabilityVersion = "1",
                pluginId = "regina-maria",
                pluginVersion = "1.0.0",
                accountId = "rm-owner",
                target = "appointments:list",
                input = new { upcoming = true },
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("plugin_module_unavailable", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(runtime.Calls);
    }

    [Fact]
    public async Task Generic_chat_requires_account_and_persists_provider_canonical_action()
    {
        using var module = ModuleRegistry.Create();
        var runtime = new ListMcpRuntime();
        await using var host = await TestHost.StartAsync(module.Registry, runtime);
        await host.SeedAsync(includeSecondAccount: true);
        host.Custody.ResetReads();
        var owner = TestHost.Owner();
        var store = host.Store;
        var now = DateTimeOffset.UtcNow;
        await store.AddConversationAsync(new(owner, "rm-chat", "RM Chat", "ACTIVE", null, now, now, 1));
        await store.ReplaceConversationGrantsAsync(
            owner,
            "rm-chat",
            1,
            ["rm-owner", "rm-spouse"],
            [
                ("reginamaria.appointment.propose_book", "1"),
                ("reginamaria.appointment.book", "1"),
            ]);
        var tools = await R2ProductEndpoints.ChatToolsAsync(store, owner, "rm-chat", CancellationToken.None, module.Registry);
        Assert.Contains(tools.Definitions, definition => JsonSerializer.Serialize(definition).Contains("book_regina_maria_appointment", StringComparison.Ordinal));

        var missing = await R2ProductEndpoints.ExecuteChatToolAsync(
            store,
            host.Custody,
            new NullTransport(),
            owner,
            "execution-missing",
            "rm-chat",
            "message",
            tools,
            JsonSerializer.SerializeToElement(new { id = "missing", name = "book_regina_maria_appointment", arguments = BookingArguments(null, "Model Doctor") }),
            1,
            CancellationToken.None,
            module.Registry,
            runtime);
        Assert.Contains("account_ambiguous", missing.Result.OutputJson, StringComparison.Ordinal);

        var proposed = await R2ProductEndpoints.ExecuteChatToolAsync(
            store,
            host.Custody,
            new NullTransport(),
            owner,
            "execution-book",
            "rm-chat",
            "message",
            tools,
            JsonSerializer.SerializeToElement(new { id = "book", name = "book_regina_maria_appointment", arguments = BookingArguments("rm-owner", "Model Doctor") }),
            2,
            CancellationToken.None,
            module.Registry,
            runtime);
        Assert.NotNull(proposed.Part.ActionId);
        var durable = await ((IDurableExecutionRequestRepository)store).GetAsync(owner, proposed.Part.ActionId!);
        Assert.NotNull(durable);
        Assert.Equal("Provider Doctor", durable!.Input.GetProperty("doctor").GetString());
        Assert.Equal("Provider Clinic", durable.Input.GetProperty("location").GetString());
        Assert.DoesNotContain("rm_create_appointment", runtime.Calls);
        Assert.Equal(0, host.Custody.ReadCount);

        var action = await ((IActionRepository)store).GetAsync(owner, proposed.Part.ActionId!);
        Assert.NotNull(action);
        host.Custody.OnRead = async () =>
            Assert.Equal(
                ActionState.Started,
                (await ((IActionRepository)store).GetAsync(owner, proposed.Part.ActionId!))!.State);
        var approval = await host.SendAsync(
            $"/api/v1/actions/{proposed.Part.ActionId}/approve",
            new { expectedVersion = action!.Version });
        Assert.Equal(HttpStatusCode.Accepted, approval.StatusCode);
        Assert.True(host.Custody.ReadCount >= 2);
        host.Custody.OnRead = null;

        Assert.True(await store.SetPluginEnabledAsync(owner, "regina-maria", "1.0.0", 1, false));
        var disabledTools = await R2ProductEndpoints.ChatToolsAsync(
            store,
            owner,
            "rm-chat",
            CancellationToken.None,
            module.Registry);
        Assert.DoesNotContain(
            "book_regina_maria_appointment",
            JsonSerializer.Serialize(disabledTools.Definitions),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generic_job_uses_explicit_account_grant_and_exposes_no_rm_writes()
    {
        using var module = ModuleRegistry.Create();
        var runtime = new ListMcpRuntime();
        await using var host = await TestHost.StartAsync(module.Registry, runtime);
        await host.SeedAsync();
        var owner = TestHost.Owner();
        var store = host.Store;
        var now = DateTimeOffset.UtcNow;
        var schedule = new JobSchedule("once", now.AddHours(1), null, "UTC", null);
        var job = new ProductJob(
            owner,
            "rm-job",
            "RM monitor",
            "List appointments",
            "ACTIVE",
            "READY",
            null,
            schedule,
            schedule.At,
            "{}",
            ["rm-owner"],
            [("reginamaria.appointments.list", "1")],
            [],
            now,
            now,
            1);
        await store.AddJobAsync(job);
        var run = await store.CreateRunOccurrenceAsync(owner, job.JobId, now) ?? throw new InvalidOperationException();
        var fence = await store.AcquireRunLeaseAsync(owner, run.RunId, "test", now, TimeSpan.FromMinutes(2))
            ?? throw new InvalidOperationException();
        Assert.True(await store.StartRunAsync(owner, run.RunId, run.Version, fence, now));
        run = await store.GetJobRunAsync(owner, run.RunId) ?? throw new InvalidOperationException();

        var tools = await R2ProductEndpoints.JobToolsAsync(store, job, CancellationToken.None, module.Registry);
        var serialized = JsonSerializer.Serialize(tools.Definitions);
        Assert.Contains("list_regina_maria_appointments", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("book_regina_maria_appointment", serialized, StringComparison.Ordinal);
        var outcome = await R2ProductEndpoints.ExecuteJobToolAsync(
            store,
            host.Custody,
            new NullTransport(),
            job,
            run,
            fence,
            tools,
            JsonSerializer.SerializeToElement(new
            {
                id = "job-call",
                name = "list_regina_maria_appointments",
                arguments = new { accountId = "rm-owner", upcoming = true },
            }),
            CancellationToken.None,
            module.Registry,
            runtime);
        Assert.Null(outcome.ErrorCode);
        Assert.False(outcome.WaitingForApproval);
        Assert.Contains("appointment-1", outcome.Result.OutputJson, StringComparison.Ordinal);
        var persistedCall = Assert.Single(
            await store.ListCapabilityCallsAsync(owner, run.RunId),
            call => call.AccountId == "rm-owner" && call.CapabilityId == "reginamaria.appointments.list");
        Assert.Equal("rm-owner", persistedCall.ExternalServerId);
        Assert.Equal("reginamaria-mcp", persistedCall.ExternalServerName);
        Assert.Equal("0.5.42", persistedCall.ExternalServerVersion);
        Assert.Equal("rm_list_appointments", persistedCall.ExternalToolName);
    }

    private static object BookingArguments(string? accountId, string doctor) => new
    {
        accountId,
        slotReceipt = "signed-slot",
        intervalId = "slot-1",
        physicianId = "doctor-1",
        serviceId = "service-1",
        service = "Consultation",
        doctor,
        specialty = "Cardiology",
        location = "Provider Clinic",
        date = "2026-08-20",
        time = "17:00",
        mode = "in-clinic",
        price = 123,
        currency = "RON",
    };

    private sealed class TestHost(WebApplication app, string directory, CountingCredentialStore custody) : IAsyncDisposable
    {
        public HttpClient Client { get; } = new() { BaseAddress = new Uri(app.Urls.Single()) };
        public SqliteKernelStore Store => app.Services.GetRequiredService<SqliteKernelStore>();
        public CountingCredentialStore Custody => custody;

        public Task RunReginaMariaHealthPassAsync(IMcpClientRuntime runtime)
            => new ReginaMariaPluginHealthService(
                app.Services.GetRequiredService<IPluginAccountRuntime>(),
                runtime,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ReginaMariaPluginHealthService>.Instance)
                .HealthPassAsync(CancellationToken.None);

        public static async Task<TestHost> StartAsync(TesseraPluginRegistry registry, IMcpClientRuntime runtime)
            => await StartCoreAsync(registry, null, null, runtime);

        public static async Task<TestHost> StartFromCatalogAsync(
            string moduleRoot,
            string moduleCatalog,
            IMcpClientRuntime runtime,
            bool enableReginaMaria = false)
            => await StartCoreAsync(null, moduleRoot, moduleCatalog, runtime, enableReginaMaria);

        private static async Task<TestHost> StartCoreAsync(
            TesseraPluginRegistry? registry,
            string? moduleRoot,
            string? moduleCatalog,
            IMcpClientRuntime runtime,
            bool enableReginaMaria = false)
        {
            var directory = Directory.CreateTempSubdirectory("tessera-rm-plugin-host").FullName;
            var port = FreePort();
            var configPath = Path.Combine(directory, "tessera.json");
            var configuration = new Dictionary<string, object?>
            {
                ["server"] = new { host = "127.0.0.1", port },
                ["identity"] = new { mode = "dev", trustDomain = "tessera.local" },
                ["policy"] = new { @default = "deny" },
                ["audit"] = new { enabled = false },
            };
            if (enableReginaMaria)
                configuration["reginaMaria"] = new
                {
                    enabled = true,
                    actionCredentialRef = "rm-action",
                    connectors = new[]
                    {
                        new
                        {
                            id = "account-a",
                            displayName = "My Regina Maria",
                            endpoint = "https://rm.example/mcp",
                        },
                    },
                };
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(configuration));
            var grantsPath = Path.Combine(directory, "grants.json");
            await File.WriteAllTextAsync(grantsPath, "{\"grants\":[],\"bindings\":[],\"recipes\":[]}");
            var custody = new CountingCredentialStore();
            var app = await BrokerHost.BuildAppAsync(new BrokerHostOptions
            {
                ConfigPath = configPath,
                PolicyPath = grantsPath,
                StoreOverride = custody,
                TransportOverride = new NullTransport(),
                ProductDatabasePath = Path.Combine(directory, "product.db"),
                PluginRoot = moduleCatalog is null ? Path.Combine(directory, "no-declarative-catalog") : RepositoryPluginRoot(),
                PluginModuleRoot = moduleRoot,
                PluginModuleCatalogPath = moduleCatalog,
                PluginRegistryOverride = registry,
                McpClientRuntimeOverride = runtime,
            });
            await app.StartAsync();
            return new(app, directory, custody);
        }

        private static string RepositoryPluginRoot()
        {
            var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../plugins"));
            return Directory.Exists(path) ? path : throw new DirectoryNotFoundException(path);
        }

        public async Task SeedAsync(bool includeSecondAccount = false)
        {
            var owner = Owner();
            var store = app.Services.GetRequiredService<SqliteKernelStore>();
            var plugin = new ReginaMariaPlugin();
            var now = DateTimeOffset.UtcNow;
            await store.AddAsync(PrincipalRef.Create(
                "https://dev.tessera.local",
                "dev",
                User,
                User,
                now));
            var manifestJson = JsonSerializer.Serialize(new
            {
                Id = plugin.Manifest.PluginId,
                Version = plugin.Manifest.Version,
                Name = plugin.Manifest.DisplayName,
                Publisher = "Tessera",
                MinimumTesseraVersion = "2.0.0",
                Capabilities = plugin.Manifest.Capabilities.Select(item => new
                {
                    Id = item.CapabilityId,
                    item.Version,
                    item.Description,
                    ExecutorKind = "mcp",
                    item.AccountRequired,
                    RequiredPermissions = item.RequiredPermissions,
                    SideEffectClass = item.SideEffectClass.ToString(),
                    TimeoutMilliseconds = 30_000,
                    MaxResultBytes = 512 * 1024,
                }),
            });
            await store.AddPluginInstallationAsync(new(
                owner,
                plugin.Manifest.PluginId,
                plugin.Manifest.Version,
                plugin.Manifest.DisplayName,
                "Tessera",
                "test-hash",
                manifestJson,
                "{}",
                true,
                now,
                now,
                1));
            var credentialRef = ConnectedAccountCredentialRef.Create(owner, "rm-owner");
            await custody.PutBundleAsync(credentialRef, new CredentialBundle());
            await store.AddConnectedAccountAsync(new(
                owner,
                "rm-owner",
                "regina-maria",
                plugin.Manifest.PluginId,
                plugin.Manifest.Version,
                "My Regina Maria",
                null,
                AccountLifecycle.Connected,
                credentialRef,
                AccountHealth.Healthy,
                now,
                "{\"endpoint\":\"https://rm.example/mcp\"}",
                ["reginamaria.appointments.read"],
                [new(plugin.Manifest.PluginId, plugin.Manifest.Version, "reginamaria.appointments.list", "1")],
                now,
                now,
                1));
            if (includeSecondAccount)
            {
                await custody.PutBundleAsync(
                    credentialRef,
                    new CredentialBundle(Extra: new Dictionary<string, string>
                    {
                        ["action_credential_ref"] = "rm-action",
                    }));
                await custody.PutBundleAsync("rm-action", new CredentialBundle(AccessToken: "action-token"));
                var secondRef = ConnectedAccountCredentialRef.Create(owner, "rm-spouse");
                await custody.PutBundleAsync(secondRef, new CredentialBundle());
                await store.AddConnectedAccountAsync(new(
                    owner,
                    "rm-spouse",
                    "regina-maria",
                    plugin.Manifest.PluginId,
                    plugin.Manifest.Version,
                    "Wife - Regina Maria",
                    null,
                    AccountLifecycle.Connected,
                    secondRef,
                    AccountHealth.Healthy,
                    now,
                    "{\"endpoint\":\"https://rm-spouse.example/mcp\"}",
                    ["reginamaria.appointments.write"],
                    [new(plugin.Manifest.PluginId, plugin.Manifest.Version, "reginamaria.appointment.book", "1")],
                    now,
                    now,
                    1));
                var current = await store.GetConnectedAccountAsync(owner, "rm-owner") ?? throw new InvalidOperationException();
                await store.SetConnectedAccountValidationAsync(
                    owner,
                    current.AccountId,
                    current.Version,
                    current.Lifecycle,
                    current.Health,
                    "owner-provider-id",
                    current.DisplayName,
                    ["reginamaria.appointments.read", "reginamaria.appointments.write"],
                    [],
                    [
                        new(plugin.Manifest.PluginId, plugin.Manifest.Version, "reginamaria.appointments.list", "1"),
                        new(plugin.Manifest.PluginId, plugin.Manifest.Version, "reginamaria.appointment.propose_book", "1"),
                        new(plugin.Manifest.PluginId, plugin.Manifest.Version, "reginamaria.appointment.book", "1"),
                    ],
                    now);
            }
        }

        public async Task<HttpResponseMessage> SendAsync(string path, object body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(body),
            };
            request.Headers.Add("X-Tessera-Dev-Principal", User);
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            return await Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }

        internal static string Owner() => PrincipalRef.Create(
            "https://dev.tessera.local",
            "dev",
            User,
            User,
            DateTimeOffset.UtcNow).PrincipalId;
    }

    private sealed class CountingCredentialStore : ICredentialStore, ICredentialWriter
    {
        private readonly InMemoryCredentialStore inner = new();

        public string Kind => inner.Kind;
        public int ReadCount { get; private set; }
        public Func<Task>? OnRead { get; set; }

        public async Task<CredentialBundle> GetBundleAsync(
            string name,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            if (OnRead is not null) await OnRead();
            return await inner.GetBundleAsync(name, cancellationToken);
        }

        public Task PutBundleAsync(
            string name,
            CredentialBundle bundle,
            CancellationToken cancellationToken = default)
            => inner.PutBundleAsync(name, bundle, cancellationToken);

        public void ResetReads() => ReadCount = 0;
    }

    private sealed class ModuleRegistry(string directory, TesseraPluginRegistry registry) : IDisposable
    {
        public TesseraPluginRegistry Registry { get; } = registry;
        public string Path => directory;
        public string CatalogPath => System.IO.Path.Combine(directory, "modules.json");

        public static ModuleRegistry Create()
        {
            var directory = Directory.CreateTempSubdirectory("tessera-rm-plugin-module").FullName;
            var plugin = new ReginaMariaPlugin();
            var bytes = File.ReadAllBytes(typeof(ReginaMariaPlugin).Assembly.Location);
            const string fileName = "Tessera.Plugins.ReginaMaria.dll";
            File.WriteAllBytes(System.IO.Path.Combine(directory, fileName), bytes);
            var installation = new PluginModuleInstallation(
                plugin.Manifest.PluginId,
                plugin.Manifest.Version,
                fileName,
                Convert.ToHexStringLower(SHA256.HashData(bytes)),
                PluginTrustState.BUILT_IN,
                plugin.Manifest.Capabilities);
            File.WriteAllText(
                System.IO.Path.Combine(directory, "modules.json"),
                JsonSerializer.Serialize(new[]
                {
                    new PluginModuleArtifact(
                        installation.PluginId,
                        installation.Version,
                        installation.AssemblyFileName,
                        installation.AssemblySha256,
                        installation.TrustState),
                }));
            return new(directory, PluginModuleDiscovery.Discover(directory, [installation]));
        }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }

    private sealed class ListMcpRuntime(
        bool incompatibleCreateOutput = false,
        bool rejectDiscovery = false) : IMcpClientRuntime
    {
        public List<string> Calls { get; } = [];
        public List<Uri> Endpoints { get; } = [];

        public Task<McpServerContract> DiscoverAsync(McpServerEndpoint endpoint, McpCallPolicy policy, CancellationToken cancellationToken = default)
        {
            if (rejectDiscovery) throw new ArgumentException("synthetic");
            Endpoints.Add(endpoint.Endpoint);
            return Task.FromResult(new McpServerContract(
                endpoint.ServerId,
                "reginamaria-mcp",
                "0.5.42",
                [
                    Tool("rm_session_status"),
                    Tool("rm_account_identity"),
                    Tool("rm_list_appointments"),
                    Tool("rm_search_slots"),
                    Tool("rm_prepare_appointment", "interval_id", "physician_id"),
                    Tool("rm_create_appointment", "interval_id", "physician_id"),
                    Tool("rm_cancel_appointment", "appointment_id"),
                ]));
        }

        public Task<McpInvocationResult> CallAsync(McpServerEndpoint endpoint, string toolName, IReadOnlyDictionary<string, object?> arguments, McpCallPolicy policy, CancellationToken cancellationToken = default)
        {
            Calls.Add(toolName);
            Endpoints.Add(endpoint.Endpoint);
            if (toolName == "rm_session_status")
                return Task.FromResult(new McpInvocationResult(
                    McpInvocationOutcome.Succeeded,
                    JsonSerializer.SerializeToElement(new { alive = true, mutations_enabled = true }),
                    null));
            if (toolName == "rm_account_identity")
                return Task.FromResult(new McpInvocationResult(
                    McpInvocationOutcome.Succeeded,
                    JsonSerializer.SerializeToElement(new
                    {
                        provider_account_id = "owner-role",
                        display_name = "Account Owner",
                    }),
                    null));
            if (toolName == "rm_prepare_appointment")
                return Task.FromResult(new McpInvocationResult(
                    McpInvocationOutcome.Succeeded,
                    JsonSerializer.SerializeToElement(new
                    {
                        bookable = true,
                        slot_receipt = "signed-slot",
                        interval_id = "slot-1",
                        physician_id = "doctor-1",
                        service_id = "service-1",
                        service = "Consultation",
                        doctor = "Provider Doctor",
                        specialty = "Cardiology",
                        location = "Provider Clinic",
                        date = "2026-08-20",
                        time = "17:00",
                        mode = "in-clinic",
                        price = 123,
                        currency = "RON",
                    }),
                    null));
            return Task.FromResult(new McpInvocationResult(
                McpInvocationOutcome.Succeeded,
                JsonSerializer.SerializeToElement(new
                {
                    count = 1,
                    appointments = new[]
                    {
                        new { id = "appointment-1", date = "2026-08-20", time = "17:00", doctor = "Provider Doctor", specialty = "Cardiology", location = "Provider Clinic", services = new[] { "Consultation" } },
                    },
                }),
                null));
        }

        private McpToolContract Tool(string name, params string[] properties)
        {
            var input = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = properties.ToDictionary(value => value, _ => (object)new { type = "string" }),
                required = properties,
                additionalProperties = false,
            });
            var output = name switch
            {
                "rm_prepare_appointment" => JsonSerializer.SerializeToElement(new
                {
                    oneOf = new[]
                    {
                        ObjectSchema(new Dictionary<string, object?>
                        {
                            ["bookable"] = new { type = "boolean", @const = true },
                            ["slot_receipt"] = new { type = "string" },
                        }),
                        ObjectSchema(new Dictionary<string, object?>
                        {
                            ["bookable"] = new { type = "boolean", @const = false },
                        }),
                    },
                }),
                "rm_create_appointment" => JsonSerializer.SerializeToElement(new
                {
                    oneOf = new[]
                    {
                        ObjectSchema(new Dictionary<string, object?>
                        {
                            ["approval_required"] = new { type = "boolean", @const = true },
                            ["action_id"] = new { type = "string" },
                        }),
                        incompatibleCreateOutput
                            ? ObjectSchema(new Dictionary<string, object?>
                            {
                                ["booked"] = new { type = "boolean", @const = false },
                            })
                            : ObjectSchema(new Dictionary<string, object?>
                            {
                                ["booked"] = new { type = "boolean", @const = true },
                                ["id"] = new { type = "string" },
                            }),
                        ObjectSchema(new Dictionary<string, object?>
                        {
                            ["booked"] = new { type = "boolean", @const = false },
                        }),
                    },
                }),
                "rm_cancel_appointment" => JsonSerializer.SerializeToElement(new
                {
                    oneOf = new[]
                    {
                        ObjectSchema(new Dictionary<string, object?>
                        {
                            ["approval_required"] = new { type = "boolean", @const = true },
                            ["action_id"] = new { type = "string" },
                        }),
                        ObjectSchema(new Dictionary<string, object?>
                        {
                            ["cancelled"] = new { type = "boolean" },
                        }),
                    },
                }),
                "rm_session_status" => ObjectSchema(new Dictionary<string, object?> { ["alive"] = new { type = "boolean" } }),
                "rm_account_identity" => ObjectSchema(new Dictionary<string, object?>
                {
                    ["provider_account_id"] = new { type = "string" },
                    ["display_name"] = new { type = "string" },
                }),
                "rm_list_appointments" => ObjectSchema(new Dictionary<string, object?> { ["appointments"] = new { type = "array" } }),
                "rm_search_slots" => ObjectSchema(new Dictionary<string, object?> { ["slots"] = new { type = "array" } }),
                _ => ObjectSchema([]),
            };

            return new(name, input, output);
        }

        private static JsonElement ObjectSchema(Dictionary<string, object?> properties)
            => JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties,
                required = properties.Keys.ToArray(),
                additionalProperties = false,
            });
    }

    private sealed class NullTransport : IHttpTransport
    {
        public Task<TransportResponse> SendAsync(string method, string url, IReadOnlyDictionary<string, string> headers, string? body, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Legacy provider transport must not be used.");
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