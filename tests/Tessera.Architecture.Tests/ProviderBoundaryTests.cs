using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Tessera.Broker;
using Tessera.Core.Identity;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Tessera.Core.Stores;
using Tessera.Persistence.Sqlite;
using Tessera.Plugin.Abstractions;
using Tessera.Providers;
using Xunit;

namespace Tessera.Architecture.Tests;

public sealed class ProviderBoundaryTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void Provider_neutral_projects_do_not_reference_plugin_assemblies()
    {
        var projects = new[]
        {
            "src/Tessera.Core/Tessera.Core.csproj",
            "src/Tessera.Broker/Tessera.Broker.csproj",
            "src/Tessera.Mcp/Tessera.Mcp.csproj",
            "src/Tessera.Mcp.Client/Tessera.Mcp.Client.csproj",
            "src/Tessera.Providers/Tessera.Providers.csproj",
            "src/Tessera.Persistence.Sqlite/Tessera.Persistence.Sqlite.csproj",
        };
        foreach (var project in projects)
        {
            var references = System.Xml.Linq.XDocument.Load(Path.Combine(Root, project))
                .Descendants("ProjectReference")
                .Select(item => item.Attribute("Include")?.Value ?? "")
                .ToArray();
            Assert.DoesNotContain(references, reference =>
                reference.Contains("Tessera.Plugins.", StringComparison.Ordinal));
        }

        var assemblies = new[]
        {
            typeof(BrokerHost).Assembly,
            typeof(PrincipalRef).Assembly,
            typeof(IHttpTransport).Assembly,
            typeof(SqliteKernelStore).Assembly,
        };
        foreach (var assembly in assemblies)
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
                reference.Name?.StartsWith("Tessera.Plugins.", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Provider_plugins_reference_only_the_plugin_abstraction_project()
    {
        foreach (var project in Directory.EnumerateFiles(
                     Path.Combine(Root, "src"),
                     "Tessera.Plugins.*.csproj",
                     SearchOption.AllDirectories))
        {
            var references = System.Xml.Linq.XDocument.Load(project)
                .Descendants("ProjectReference")
                .Select(item => Path.GetFileName(item.Attribute("Include")?.Value))
                .ToArray();
            Assert.Equal("Tessera.Plugin.Abstractions.csproj", Assert.Single(references));
        }
    }

    [Fact]
    public void Provider_implementation_types_are_absent_from_neutral_source()
    {
        var forbidden = new[]
        {
            "ReginaMariaCapabilities",
            "ReginaMariaMcpAdapter",
            "ReginaMariaAccountEndpoints",
            "ReginaMariaHealthService",
            "GmailRestAdapter",
            "GmailOAuthService",
            "GmailSyncService",
            "GmailTokenRefreshService",
            "GitHubRestAdapter",
            "OneDriveRestAdapter",
            "OneDriveOAuthService",
            "OneDriveTokenRefreshService",
            "using Tessera.Plugins.",
            "physicianId",
            "intervalId",
            "slotReceipt",
            "appointmentId",
            "gmailThreadId",
            "githubNodeId",
        };
        var directories = new[]
        {
            "src/Tessera.Core",
            "src/Tessera.Broker",
            "src/Tessera.Mcp",
            "src/Tessera.Mcp.Client",
            "src/Tessera.Providers",
            "src/Tessera.Persistence.Sqlite",
        };
        foreach (var directory in directories)
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, directory), "*.cs", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var source = File.ReadAllText(file);
            foreach (var token in forbidden)
                Assert.DoesNotContain(token, source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Provider_identifiers_are_absent_from_neutral_runtime_source()
    {
        var directories = new[]
        {
            "src/Tessera.Core",
            "src/Tessera.Broker",
            "src/Tessera.Mcp",
            "src/Tessera.Mcp.Client",
            "src/Tessera.Providers",
            "src/Tessera.Persistence.Sqlite",
        };
        var identifiers = new[] { "ReginaMaria", "regina-maria", "Gmail", "gmail", "GitHub", "github", "OneDrive", "onedrive" };
        foreach (var directory in directories)
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, directory), "*.cs", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var relative = Path.GetRelativePath(Root, file).Replace(Path.DirectorySeparatorChar, '/');
            var source = File.ReadAllText(file);
            foreach (var identifier in identifiers)
            {
                if (relative == "src/Tessera.Core/Product/ProductContentValidation.cs"
                    && identifier == "github")
                    continue;
                if (relative == "src/Tessera.Persistence.Sqlite/KernelMigrations.cs"
                    && identifier == "gmail")
                    continue;
                Assert.DoesNotContain(identifier, source, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task Zero_provider_host_starts_local_capability_works_and_history_survives()
    {
        await using var fixture = await EmptyPluginHost.StartAsync();
        var owner = fixture.Owner;
        var now = DateTimeOffset.UtcNow;
        var localManifest = File.ReadAllText(Path.Combine(Root, "plugins/local/manifest.json"));
        await fixture.Store.AddPluginInstallationAsync(new(
            owner,
            "local",
            "1.0.0",
            "Local",
            "Tessera",
            "test",
            localManifest,
            "{}",
            true,
            now,
            now,
            1));
        var historical = EvidenceRecord.Create(
            "historical-provider-evidence",
            owner,
            "capability.result",
            "old-call",
            "tessera://capability/removed/old-call",
            now,
            now,
            "sha256",
            1,
            new string('a', 64),
            RetentionState.Active,
            SensitivityClass.Internal,
            ProducerRef.Create("plugin:removed", "1.0.0"),
            1);
        await ((IEvidenceRepository)fixture.Store).AddAsync(owner, historical);

        Assert.Equal(HttpStatusCode.OK, (await fixture.Client.GetAsync("/healthz")).StatusCode);
        var local = await fixture.SendAsync(
            "/api/v1/capabilities/local.time/invoke",
            new
            {
                capabilityId = "local.time",
                capabilityVersion = "1",
                pluginId = "local",
                pluginVersion = "1.0.0",
                accountId = (string?)null,
                target = "UTC",
                input = new { timeZone = "UTC" },
            });
        Assert.Equal(HttpStatusCode.OK, local.StatusCode);
        Assert.NotNull(await ((IEvidenceRepository)fixture.Store).GetAsync(owner, historical.EvidenceId));

        foreach (var plugin in new[] { "gmail", "regina-maria", "github" })
        {
            var unavailable = await fixture.SendAsync(
                $"/api/v1/capabilities/{plugin}.missing/invoke",
                new
                {
                    capabilityId = $"{plugin}.missing",
                    capabilityVersion = "1",
                    pluginId = plugin,
                    pluginVersion = "1.0.0",
                    accountId = "missing",
                    target = "missing",
                    input = new { },
                });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, unavailable.StatusCode);
            Assert.Contains("plugin_module_unavailable", await unavailable.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Missing_executable_plugin_blocks_only_dependent_jobs()
    {
        await using var fixture = await EmptyPluginHost.StartAsync();
        var owner = fixture.Owner;
        var now = DateTimeOffset.UtcNow;
        await fixture.Store.AddPluginInstallationAsync(new(
            owner,
            "gmail",
            "1.0.0",
            "Gmail",
            "Tessera",
            "test",
            File.ReadAllText(Path.Combine(Root, "plugins/gmail/manifest.json")),
            "{}",
            true,
            now,
            now,
            1));
        await fixture.Store.AddPluginInstallationAsync(new(
            owner,
            "local",
            "1.0.0",
            "Local",
            "Tessera",
            "test",
            File.ReadAllText(Path.Combine(Root, "plugins/local/manifest.json")),
            "{}",
            true,
            now,
            now,
            1));
        var schedule = new JobSchedule("once", now.AddHours(1), null, "UTC", null);
        await fixture.Store.AddJobAsync(new(
            owner,
            "gmail-job",
            "Gmail",
            "Search mail",
            "ACTIVE",
            "READY",
            null,
            schedule,
            schedule.At,
            "{}",
            [],
            [("gmail.messages.search", "1")],
            [],
            now,
            now,
            1));
        await fixture.Store.AddJobAsync(new(
            owner,
            "local-job",
            "Local",
            "Read time",
            "ACTIVE",
            "READY",
            null,
            schedule,
            schedule.At,
            "{}",
            [],
            [("local.time", "1")],
            [],
            now,
            now,
            1));

        await fixture.Store.RecomputeJobsHealthAsync(
            owner,
            new HashSet<(string Id, string Version)>(),
            CancellationToken.None);

        Assert.Equal("BLOCKED", (await fixture.Store.GetJobAsync(owner, "gmail-job"))!.Health);
        Assert.Equal("READY", (await fixture.Store.GetJobAsync(owner, "local-job"))!.Health);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tessera.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class EmptyPluginHost(
        WebApplication app,
        string directory,
        string owner) : IAsyncDisposable
    {
        public HttpClient Client { get; } = new() { BaseAddress = new Uri(app.Urls.Single()) };
        public SqliteKernelStore Store => app.Services.GetRequiredService<SqliteKernelStore>();
        public string Owner { get; } = owner;

        public static async Task<EmptyPluginHost> StartAsync()
        {
            var directory = Directory.CreateTempSubdirectory("tessera-zero-plugin").FullName;
            var port = FreePort();
            var configPath = Path.Combine(directory, "tessera.json");
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
            {
                server = new { host = "127.0.0.1", port },
                identity = new { mode = "dev", trustDomain = "tessera.local" },
                policy = new { @default = "deny" },
                audit = new { enabled = false },
            }));
            var grants = Path.Combine(directory, "grants.json");
            await File.WriteAllTextAsync(grants, "{\"grants\":[],\"bindings\":[],\"recipes\":[]}");
            var app = await BrokerHost.BuildAppAsync(new BrokerHostOptions
            {
                ConfigPath = configPath,
                PolicyPath = grants,
                StoreOverride = new InMemoryCredentialStore(),
                TransportOverride = new NullTransport(),
                ProductDatabasePath = Path.Combine(directory, "product.db"),
                PluginRoot = Path.Combine(directory, "no-plugins"),
                PluginRegistryOverride = TesseraPluginRegistry.AuthoritativeEmpty,
            });
            await app.StartAsync();
            var principal = PrincipalRef.Create(
                "https://dev.tessera.local",
                "dev",
                "owner@example.com",
                "owner@example.com",
                DateTimeOffset.UtcNow);
            await app.Services.GetRequiredService<SqliteKernelStore>().AddAsync(principal);
            return new(app, directory, principal.PrincipalId);
        }

        public async Task<HttpResponseMessage> SendAsync(string path, object body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(body),
            };
            request.Headers.Add("X-Tessera-Dev-Principal", "owner@example.com");
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

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }

    private sealed class NullTransport : IHttpTransport
    {
        public Task<TransportResponse> SendAsync(
            string method,
            string url,
            IReadOnlyDictionary<string, string> headers,
            string? body,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Provider transport must not be used.");
    }
}