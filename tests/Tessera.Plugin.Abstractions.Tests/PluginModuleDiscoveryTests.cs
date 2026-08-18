using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Tessera.Core.Stores;
using Tessera.Mcp.Client;
using Tessera.Providers;
using Xunit;

namespace Tessera.Plugin.Abstractions.Tests;

public sealed class PluginModuleDiscoveryTests
{
    [Fact]
    public void Missing_module_root_is_normal()
    {
        var registry = PluginModuleDiscovery.Discover(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            [Installation("missing.dll", new string('0', 64))]);

        Assert.Empty(registry.ListManifests());
    Assert.True(registry.IsAuthoritative);
    }

    [Fact]
    public void Discovers_hash_pinned_module_and_preserves_authoritative_risk_overlays()
    {
        using var directory = ModuleDirectory.Create();
        var registry = PluginModuleDiscovery.Discover(directory.Path, [directory.Installation]);

        var manifest = Assert.Single(registry.ListManifests());
        Assert.Equal(["neutral.read", "neutral.write"], manifest.Capabilities.Select(item => item.CapabilityId));
        Assert.True(registry.TryResolve("neutral.fixture", "1.0.0", out _));

        var write = manifest.Capabilities.Single(item => item.CapabilityId == "neutral.write");
        Assert.Equal(SideEffectClass.ExternalReversible, write.SideEffectClass);
        Assert.True(write.AccountRequired);
        Assert.Equal(["records:write"], write.RequiredPermissions);
        Assert.Equal([SensitivityClass.Confidential], write.AllowedDataClasses);
        Assert.Equal(IdempotencySupport.Keyed, write.IdempotencySupport);
        Assert.Equal(VerificationSupport.ProviderState, write.VerificationSupport);
    }

    [Theory]
    [InlineData(PluginTrustState.UNTRUSTED)]
    [InlineData(PluginTrustState.DISABLED)]
    public void Non_executable_trust_states_do_not_load_modules(PluginTrustState trustState)
    {
        var root = Directory.CreateTempSubdirectory("tessera-disabled-module").FullName;
        try
        {
            var registry = PluginModuleDiscovery.Discover(root,
                [Installation("absent.dll", new string('0', 64)) with { TrustState = trustState }]);
            Assert.Empty(registry.ListManifests());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Undefined_trust_state_fails_closed()
    {
        var installation = Installation("absent.dll", new string('0', 64)) with
        {
            TrustState = (PluginTrustState)int.MaxValue,
        };
        var exception = Assert.Throws<PluginModuleException>(() =>
            PluginModuleDiscovery.Discover(Path.GetTempPath(), [installation]));
        Assert.Equal("invalid_module_trust", exception.ErrorCode);
    }

    [Fact]
    public void Duplicate_installation_identity_fails_before_publication()
    {
        var installation = Installation("one.dll", new string('0', 64));
        var exception = Assert.Throws<PluginModuleException>(() =>
            PluginModuleDiscovery.Discover(Path.GetTempPath(), [installation, installation with { AssemblyFileName = "two.dll" }]));
        Assert.Equal("duplicate_installation_identity", exception.ErrorCode);
    }

    [Fact]
    public void Malformed_assembly_fails_the_whole_discovery()
    {
        using var directory = ModuleDirectory.Create();
        var malformed = Encoding.UTF8.GetBytes("not an assembly");
        File.WriteAllBytes(System.IO.Path.Combine(directory.Path, "z-bad.dll"), malformed);
        var bad = Installation("z-bad.dll", Convert.ToHexStringLower(SHA256.HashData(malformed))) with
        {
            PluginId = "neutral.bad",
        };

        var exception = Assert.Throws<PluginModuleException>(() =>
            PluginModuleDiscovery.Discover(directory.Path, [directory.Installation, bad]));
        Assert.Equal("malformed_module", exception.ErrorCode);
    }

    [Fact]
    public void Traversal_symlink_and_hash_mismatch_fail_closed()
    {
        using var directory = ModuleDirectory.Create();
        var traversal = directory.Installation with { AssemblyFileName = "../module.dll" };
        Assert.Equal("invalid_module_path", Assert.Throws<PluginModuleException>(() =>
            PluginModuleDiscovery.Discover(directory.Path, [traversal])).ErrorCode);

        var linkPath = System.IO.Path.Combine(directory.Path, "link.dll");
        File.CreateSymbolicLink(linkPath, System.IO.Path.Combine(directory.Path, directory.Installation.AssemblyFileName));
        var link = directory.Installation with { AssemblyFileName = "link.dll" };
        Assert.Equal("invalid_module_file", Assert.Throws<PluginModuleException>(() =>
            PluginModuleDiscovery.Discover(directory.Path, [link])).ErrorCode);

        var wrongHash = directory.Installation with { AssemblySha256 = new string('0', 64) };
        Assert.Equal("module_hash_mismatch", Assert.Throws<PluginModuleException>(() =>
            PluginModuleDiscovery.Discover(directory.Path, [wrongHash])).ErrorCode);
    }

    [Fact]
    public void Identity_version_and_capability_mismatches_fail_closed()
    {
        using var directory = ModuleDirectory.Create();
        var version = directory.Installation with { Version = "2.0.0" };
        Assert.Equal("module_identity_mismatch", Assert.Throws<PluginModuleException>(() =>
            PluginModuleDiscovery.Discover(directory.Path, [version])).ErrorCode);

        var changed = NeutralFixturePlugin.Capabilities
            .Select(item => item.CapabilityId == "neutral.read" ? item with { AccountRequired = true } : item)
            .ToArray();
        var capability = directory.Installation with { Capabilities = changed };
        Assert.Equal("module_capability_mismatch", Assert.Throws<PluginModuleException>(() =>
            PluginModuleDiscovery.Discover(directory.Path, [capability])).ErrorCode);
    }

    [Fact]
    public async Task Extra_tools_remain_invisible_and_duplicate_required_tools_fail_closed()
    {
        using var directory = ModuleDirectory.Create();
        var registry = PluginModuleDiscovery.Discover(directory.Path, [directory.Installation]);
        PluginCapabilityContext Context(IMcpClientRuntime runtime) => new(
            null,
            CredentialBundle.Empty,
            new NullTransport(),
            runtime,
            (_, _) => ValueTask.FromResult(CredentialBundle.Empty));

        var capability = await registry.CreateCapabilityAsync(
            "neutral.fixture", "1.0.0", "neutral.read", "1.0.0",
            Context(new NullMcpRuntime(includeExtraTool: true)));
        Assert.Equal("neutral.read", capability.Descriptor.CapabilityId);

        var exception = await Assert.ThrowsAsync<PluginModuleException>(async () =>
            await registry.CreateCapabilityAsync(
                "neutral.fixture", "1.0.0", "neutral.read", "1.0.0",
                Context(new NullMcpRuntime(duplicateRequiredTool: true))));
        Assert.Equal("mcp_toolset_incompatible", exception.ErrorCode);
    }

    [Fact]
    public void Candidate_count_is_bounded_even_when_modules_are_missing()
    {
        var installations = Enumerable.Range(0, PluginModuleDiscovery.MaximumModules + 1)
            .Select(index => Installation($"module-{index:D3}.dll", new string('0', 64)) with
            {
                PluginId = $"neutral.module-{index:D3}",
            })
            .ToArray();
        var exception = Assert.Throws<PluginModuleException>(() =>
            PluginModuleDiscovery.Discover(Path.GetTempPath(), installations));
        Assert.Equal("module_bound_exceeded", exception.ErrorCode);
    }

    [Fact]
    public void Artifact_catalog_discovers_pinned_module_without_duplicate_capability_metadata()
    {
        using var directory = ModuleDirectory.Create();
        var artifact = new PluginModuleArtifact(
            "neutral.fixture",
            "1.0.0",
            directory.Installation.AssemblyFileName,
            directory.Installation.AssemblySha256,
            PluginTrustState.BUILT_IN);
        var catalogPath = System.IO.Path.Combine(directory.Path, "modules.json");
        File.WriteAllText(catalogPath, JsonSerializer.Serialize(new[] { artifact }));

        var loaded = PluginModuleDiscovery.LoadArtifactCatalog(catalogPath);
        var registry = PluginModuleDiscovery.DiscoverArtifacts(directory.Path, loaded);

        Assert.True(registry.IsAuthoritative);
        Assert.Equal("neutral.fixture", Assert.Single(registry.ListManifests()).PluginId);
    }

    [Fact]
    public void Artifact_catalog_rejects_unknown_fields()
    {
        var path = System.IO.Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "[{\"PluginId\":\"neutral.fixture\",\"Version\":\"1.0.0\",\"AssemblyFileName\":\"x.dll\",\"AssemblySha256\":\"" + new string('0', 64) + "\",\"TrustState\":\"BUILT_IN\",\"Unexpected\":true}]");
            var exception = Assert.Throws<PluginModuleException>(() =>
                PluginModuleDiscovery.LoadArtifactCatalog(path));
            Assert.Equal("invalid_module_catalog", exception.ErrorCode);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Registry_creates_only_declared_capabilities_with_exact_account_binding()
    {
        using var directory = ModuleDirectory.Create();
        var registry = PluginModuleDiscovery.Discover(directory.Path, [directory.Installation]);
        var context = new PluginCapabilityContext(
            null,
            new CredentialBundle(),
            new NullTransport(),
            new NullMcpRuntime(),
            (_, _) => ValueTask.FromResult(new CredentialBundle()));

        var capability = await registry.CreateCapabilityAsync(
            "neutral.fixture", "1.0.0", "neutral.read", "1.0.0", context);
        Assert.Equal("neutral.read", capability.Descriptor.CapabilityId);

        var exception = await Assert.ThrowsAsync<PluginModuleException>(async () =>
            await registry.CreateCapabilityAsync(
                "neutral.fixture", "1.0.0", "neutral.write", "1.0.0", context));
        Assert.Equal("account_unavailable", exception.ErrorCode);
    }

    [Fact]
    public async Task Registry_denies_MCP_schema_drift_before_capability_creation()
    {
        using var directory = ModuleDirectory.Create();
        var registry = PluginModuleDiscovery.Discover(directory.Path, [directory.Installation]);
        var context = new PluginCapabilityContext(
            null,
            CredentialBundle.Empty,
            new NullTransport(),
            new NullMcpRuntime(schemaCompatible: false),
            (_, _) => ValueTask.FromResult(CredentialBundle.Empty));

        var exception = await Assert.ThrowsAsync<PluginModuleException>(async () =>
            await registry.CreateCapabilityAsync(
                "neutral.fixture",
                "1.0.0",
                "neutral.read",
                "1.0.0",
                context));
        Assert.Equal("mcp_schema_incompatible", exception.ErrorCode);
    }

    [Fact]
    public async Task Registry_denies_MCP_property_type_and_server_version_drift()
    {
        using var directory = ModuleDirectory.Create();
        var registry = PluginModuleDiscovery.Discover(directory.Path, [directory.Installation]);
        PluginCapabilityContext Context(IMcpClientRuntime runtime) => new(
            null,
            CredentialBundle.Empty,
            new NullTransport(),
            runtime,
            (_, _) => ValueTask.FromResult(CredentialBundle.Empty));

        var typeException = await Assert.ThrowsAsync<PluginModuleException>(async () =>
            await registry.CreateCapabilityAsync(
                "neutral.fixture", "1.0.0", "neutral.read", "1.0.0",
                Context(new NullMcpRuntime(propertyType: "integer"))));
        Assert.Equal("mcp_schema_incompatible", typeException.ErrorCode);

        var requiredException = await Assert.ThrowsAsync<PluginModuleException>(async () =>
            await registry.CreateCapabilityAsync(
                "neutral.fixture", "1.0.0", "neutral.read", "1.0.0",
                Context(new NullMcpRuntime(propertiesRequired: false))));
        Assert.Equal("mcp_schema_incompatible", requiredException.ErrorCode);

        var versionException = await Assert.ThrowsAsync<PluginModuleException>(async () =>
            await registry.CreateCapabilityAsync(
                "neutral.fixture", "1.0.0", "neutral.read", "1.0.0",
                Context(new NullMcpRuntime(serverVersion: "0.9.0"))));
        Assert.Equal("mcp_server_incompatible", versionException.ErrorCode);
    }

    [Fact]
    public async Task Registry_accepts_a_compatible_MCP_output_union_branch()
    {
        using var directory = ModuleDirectory.Create();
        var registry = PluginModuleDiscovery.Discover(directory.Path, [directory.Installation]);
        var context = new PluginCapabilityContext(
            null,
            CredentialBundle.Empty,
            new NullTransport(),
            new NullMcpRuntime(unionSchema: true),
            (_, _) => ValueTask.FromResult(CredentialBundle.Empty));

        var capability = await registry.CreateCapabilityAsync(
            "neutral.fixture", "1.0.0", "neutral.read", "1.0.0", context);

        Assert.Equal("neutral.read", capability.Descriptor.CapabilityId);
    }

    [Fact]
    public async Task Write_capability_defers_MCP_discovery_and_credentials_until_invocation()
    {
        using var directory = ModuleDirectory.Create();
        var registry = PluginModuleDiscovery.Discover(directory.Path, [directory.Installation]);
        var runtime = new NullMcpRuntime();
        var credentialResolutions = 0;
        var context = new PluginCapabilityContext(
            Account("account-a"),
            CredentialBundle.Empty,
            new NullTransport(),
            runtime,
            (_, _) =>
            {
                credentialResolutions++;
                return ValueTask.FromResult(CredentialBundle.Empty);
            });

        var capability = await registry.CreateCapabilityAsync(
            "neutral.fixture", "1.0.0", "neutral.write", "1.0.0", context);
        Assert.Equal(0, runtime.DiscoverCount);
        Assert.Equal(0, credentialResolutions);

        await capability.InvokeAsync(new(
            "owner",
            "write-call",
            "neutral.write",
            "1.0.0",
            "records/account-a",
            JsonSerializer.SerializeToElement(new { value = "safe" }),
            "authorization",
            "write-key"));
        Assert.Equal(1, runtime.DiscoverCount);
        Assert.Equal(0, credentialResolutions);
    }

    [Fact]
    public void Model_tools_project_accounts_and_bind_without_provider_switches()
    {
        using var directory = ModuleDirectory.Create();
        var registry = PluginModuleDiscovery.Discover(directory.Path, [directory.Installation]);
        var accounts = new[] { Account("account-b"), Account("account-a") };
        var grants = new HashSet<(string Id, string Version)>
        {
            ("neutral.read", "1.0.0"),
            ("neutral.write", "1.0.0"),
        };

        var projected = registry.ProjectModelTools(accounts, grants);
        Assert.Equal(["neutral_read", "neutral_write"], projected.Select(item => item.Tool.Name));
        var write = projected.Single(item => item.Tool.Name == "neutral_write");
        Assert.Equal(["account-a", "account-b"], write.Accounts.Select(item => item.AccountId));
        Assert.Contains("accountId", write.Parameters.GetProperty("required").EnumerateArray().Select(item => item.GetString()));

        var ambiguous = Assert.Throws<PluginModuleException>(() =>
            registry.BindModelTool(write, JsonSerializer.SerializeToElement(new { })));
        Assert.Equal("account_ambiguous", ambiguous.ErrorCode);
        var binding = registry.BindModelTool(write, JsonSerializer.SerializeToElement(new { accountId = "account-b" }));
        Assert.Equal("records/account-b", binding.TargetScope);

        var jobReadOnly = registry.ProjectModelTools(
            accounts,
            grants,
            new HashSet<string>(StringComparer.Ordinal),
            forJob: true);
        Assert.Equal(["neutral_read"], jobReadOnly.Select(item => item.Tool.Name));
    }

    private static PluginModuleInstallation Installation(string fileName, string hash) => new(
        "neutral.fixture",
        "1.0.0",
        fileName,
        hash,
        PluginTrustState.BUILT_IN,
        NeutralFixturePlugin.Capabilities);

    private static ConnectedAccount Account(string accountId)
    {
        var now = DateTimeOffset.UtcNow;
        return new(
            "owner",
            accountId,
            "neutral",
            "neutral.fixture",
            "1.0.0",
            accountId,
            null,
            AccountLifecycle.Connected,
            $"credential-{accountId}",
            AccountHealth.Healthy,
            now,
            "{}",
            ["records:read", "records:write"],
            [
                new("neutral.fixture", "1.0.0", "neutral.read", "1.0.0"),
                new("neutral.fixture", "1.0.0", "neutral.write", "1.0.0"),
            ],
            now,
            now,
            1);
    }

    private sealed class ModuleDirectory : IDisposable
    {
        private ModuleDirectory(string path, PluginModuleInstallation installation)
        {
            Path = path;
            Installation = installation;
        }

        public string Path { get; }
        public PluginModuleInstallation Installation { get; }

        public static ModuleDirectory Create()
        {
            var path = Directory.CreateTempSubdirectory("tessera-module-discovery").FullName;
            var bytes = File.ReadAllBytes(typeof(NeutralFixturePlugin).Assembly.Location);
            const string fileName = "neutral-module.dll";
            File.WriteAllBytes(System.IO.Path.Combine(path, fileName), bytes);
            return new(path, PluginModuleDiscoveryTests.Installation(
                fileName,
                Convert.ToHexStringLower(SHA256.HashData(bytes))));
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class NullTransport : IHttpTransport
    {
        public Task<TransportResponse> SendAsync(
            string method,
            string url,
            IReadOnlyDictionary<string, string> headers,
            string? body,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class NullMcpRuntime(
        bool schemaCompatible = true,
        string propertyType = "string",
        string serverVersion = "1.0.0",
        bool includeExtraTool = false,
        bool duplicateRequiredTool = false,
        bool propertiesRequired = true,
        bool unionSchema = false) : IMcpClientRuntime
    {
        public int DiscoverCount { get; private set; }

        public Task<McpServerContract> DiscoverAsync(
            McpServerEndpoint endpoint,
            McpCallPolicy policy,
            CancellationToken cancellationToken = default)
        {
            DiscoverCount++;
            var properties = schemaCompatible
                ? new Dictionary<string, object?> { ["value"] = new { type = propertyType } }
                : new Dictionary<string, object?>();
            var schema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties,
                required = propertiesRequired && schemaCompatible ? new[] { "value" } : [],
                additionalProperties = false,
            });
            var output = unionSchema
                ? JsonSerializer.SerializeToElement(new
                {
                    oneOf = new object[]
                    {
                        new { type = "object", properties = new { denied = new { type = "boolean" } }, required = new[] { "denied" } },
                        schema,
                    },
                })
                : schema;
            var tools = new List<McpToolContract> { new("shared", schema, output) };
            if (includeExtraTool) tools.Add(new("unclassified_write", schema, schema));
            if (duplicateRequiredTool) tools.Add(new("shared", schema, schema));
            return Task.FromResult(new McpServerContract(
                endpoint.ServerId,
                "neutral",
                serverVersion,
                tools));
        }

        public Task<McpInvocationResult> CallAsync(
            McpServerEndpoint endpoint,
            string toolName,
            IReadOnlyDictionary<string, object?> arguments,
            McpCallPolicy policy,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}

public sealed class NeutralFixturePlugin : ITesseraCapabilityPlugin, ITesseraModelToolPlugin, ITesseraMcpPlugin
{
    internal static readonly IReadOnlyList<PluginCapabilityManifest> Capabilities =
    [
        Capability(
            "neutral.write",
            "shared",
            SideEffectClass.ExternalReversible,
            true,
            ["records:write"],
            [SensitivityClass.Confidential],
            VerificationSupport.ProviderState),
        Capability(
            "neutral.read",
            "shared",
            SideEffectClass.ReadOnly,
            false,
            ["records:read"],
            [SensitivityClass.Internal],
            VerificationSupport.None),
    ];

    public TesseraPluginManifest Manifest { get; } = new(
        "neutral.fixture",
        "1.0.0",
        "Neutral fixture",
        "neutral",
        Capabilities);

    public IReadOnlyList<PluginModelToolManifest> ModelTools { get; } =
    [
        new("neutral_read", "neutral.read", "1.0.0", "Read neutral records.", JsonSerializer.SerializeToElement(new { type = "object", properties = new { }, required = Array.Empty<string>(), additionalProperties = false })),
        new("neutral_write", "neutral.write", "1.0.0", "Write neutral records.", JsonSerializer.SerializeToElement(new { type = "object", properties = new { }, required = Array.Empty<string>(), additionalProperties = false })),
    ];

    public RequiredMcpServer RequiredMcpServer { get; } = new("neutral", "1.0.0");

    public IReadOnlyList<RequiredMcpTool> RequiredMcpTools { get; } =
        [new("shared", [new("value", "string")], [new("value", "string")])];

    public async ValueTask<McpServerContract> DiscoverMcpAsync(
        PluginCapabilityContext context,
        CancellationToken cancellationToken = default)
        => await context.McpRuntime.DiscoverAsync(
            new("neutral", new Uri("https://neutral.example/mcp")),
            McpCallPolicy.ReadOnly,
            cancellationToken);

    public PluginModelToolBinding BindModelTool(string modelToolName, JsonElement arguments, ConnectedAccount? account)
        => new(account?.AccountId, $"records/{account?.AccountId ?? "none"}", arguments.Clone());

    public ValueTask<ICapability> CreateCapabilityAsync(
        string capabilityId,
        string capabilityVersion,
        PluginCapabilityContext context,
        CancellationToken cancellationToken = default)
    {
        var manifest = Capabilities.Single(item => item.CapabilityId == capabilityId && item.Version == capabilityVersion);
        return ValueTask.FromResult<ICapability>(new NeutralCapability(manifest.ToDescriptor()));
    }

    private sealed class NeutralCapability(CapabilityDescriptor descriptor) : ICapability
    {
        public CapabilityDescriptor Descriptor { get; } = descriptor;

        public ValueTask<CapabilityResult> InvokeAsync(
            CapabilityInvocation invocation,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new CapabilityResult(
                CapabilityOutcome.Succeeded,
                JsonSerializer.SerializeToElement(new { ok = true }),
                null,
                null,
                null));
    }

    private static PluginCapabilityManifest Capability(
        string capabilityId,
        string toolName,
        SideEffectClass sideEffectClass,
        bool accountRequired,
        IReadOnlyList<string> permissions,
        IReadOnlyList<SensitivityClass> sensitivity,
        VerificationSupport verification) => new(
            capabilityId,
            "1.0.0",
            $"Execute {capabilityId}.",
            toolName,
            JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false }),
            JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false }),
            sideEffectClass,
            accountRequired,
            permissions,
            sensitivity,
            IdempotencySupport.Keyed,
            verification);
}