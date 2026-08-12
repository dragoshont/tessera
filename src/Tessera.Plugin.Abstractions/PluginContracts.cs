using System.Text.Json;
using System.Text.Json.Nodes;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Tessera.Core.Stores;
using Tessera.Mcp.Client;
using Tessera.Providers;

namespace Tessera.Plugin.Abstractions;

public sealed record PluginCapabilityManifest(
    string CapabilityId,
    string Version,
    string Description,
    string ExternalToolName,
    JsonElement InputSchema,
    JsonElement OutputSchema,
    SideEffectClass SideEffectClass,
    bool AccountRequired,
    IReadOnlyList<string> RequiredPermissions,
    IReadOnlyList<SensitivityClass> AllowedDataClasses,
    IdempotencySupport IdempotencySupport,
    VerificationSupport VerificationSupport)
{
    public CapabilityDescriptor ToDescriptor() => CapabilityDescriptor.Create(
        CapabilityId,
        Version,
        Description,
        InputSchema.GetRawText(),
        OutputSchema.GetRawText(),
        SideEffectClass,
        RequiredPermissions,
        AllowedDataClasses,
        IdempotencySupport,
        VerificationSupport);
}

public sealed record TesseraPluginManifest(
    string PluginId,
    string Version,
    string DisplayName,
    string ProviderId,
    IReadOnlyList<PluginCapabilityManifest> Capabilities);

public sealed record PluginCapabilityContext(
    ConnectedAccount? Account,
    CredentialBundle AccountCredential,
    IHttpTransport Transport,
    IMcpClientRuntime McpRuntime,
    Func<string, CancellationToken, ValueTask<CredentialBundle>> ResolveCredentialAsync);

public interface ITesseraCapabilityPlugin
{
    TesseraPluginManifest Manifest { get; }

    ValueTask<ICapability> CreateCapabilityAsync(
        string capabilityId,
        string capabilityVersion,
        PluginCapabilityContext context,
        CancellationToken cancellationToken = default);
}

public sealed class DeferredPluginCapability(
    CapabilityDescriptor descriptor,
    Func<CancellationToken, ValueTask<ICapability>> createAsync) : ICapability
{
    public CapabilityDescriptor Descriptor { get; } = descriptor
        ?? throw new ArgumentNullException(nameof(descriptor));

    public async ValueTask<CapabilityResult> InvokeAsync(
        CapabilityInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        var capability = await createAsync(cancellationToken).ConfigureAwait(false);
        if (capability.Descriptor.CapabilityId != Descriptor.CapabilityId
            || capability.Descriptor.Version != Descriptor.Version)
            throw new PluginModuleException("capability_implementation_mismatch");
        return await capability.InvokeAsync(invocation, cancellationToken).ConfigureAwait(false);
    }
}

public sealed record PluginModelToolManifest(
    string Name,
    string CapabilityId,
    string CapabilityVersion,
    string Description,
    JsonElement InputSchema,
    bool JobEligible = true,
    string? ProposalCapabilityId = null,
    string? ProposalCapabilityVersion = null);

public sealed record PluginModelToolBinding(
    string? AccountId,
    string TargetScope,
    JsonElement Input);

public interface ITesseraModelToolPlugin
{
    IReadOnlyList<PluginModelToolManifest> ModelTools { get; }

    PluginModelToolBinding BindModelTool(
        string modelToolName,
        JsonElement arguments,
        ConnectedAccount? account);
}

public sealed record RequiredMcpServer(string Name, string Version);

public sealed record RequiredMcpProperty(string Name, string Type);

public sealed record RequiredMcpTool(
    string Name,
    IReadOnlyList<RequiredMcpProperty> RequiredInputProperties,
    IReadOnlyList<RequiredMcpProperty> RequiredOutputProperties);

public interface ITesseraMcpPlugin
{
    RequiredMcpServer RequiredMcpServer { get; }

    IReadOnlyList<RequiredMcpTool> RequiredMcpTools { get; }

    ValueTask<McpServerContract> DiscoverMcpAsync(
        PluginCapabilityContext context,
        CancellationToken cancellationToken = default);
}

public sealed record PluginAccountDefinition(
    string ProviderId,
    IReadOnlyList<string> InitialPermissions,
    IReadOnlyList<AccountCapabilityBinding> CapabilityBindings);

public sealed record PluginAccountValidation(
    AccountLifecycle Lifecycle,
    AccountHealth Health,
    string? ProviderAccountId,
    string? IdentityHint,
    IReadOnlyList<string> Permissions,
    IReadOnlyList<string> ProviderScopes,
    IReadOnlyList<AccountCapabilityBinding> CapabilityBindings,
    DateTimeOffset? LastSuccessfulUse);

public interface ITesseraAccountPlugin
{
    PluginAccountDefinition DefineAccount(string pluginVersion, JsonElement nonSecretConfiguration);

    ValueTask<PluginAccountValidation> ValidateAccountAsync(
        ConnectedAccount account,
        CredentialBundle credential,
        PluginCapabilityContext context,
        CancellationToken cancellationToken = default);

    ValueTask DisconnectAccountAsync(
        ConnectedAccount account,
        CredentialBundle credential,
        PluginCapabilityContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}

public sealed record ProjectedModelTool(
    string PluginId,
    string PluginVersion,
    PluginModelToolManifest Tool,
    PluginCapabilityManifest Capability,
    IReadOnlyList<ConnectedAccount> Accounts,
    JsonElement Parameters);

public sealed class TesseraPluginRegistry
{
    private sealed record Entry(
        ITesseraCapabilityPlugin Plugin,
        TesseraPluginManifest Manifest,
        IReadOnlyList<PluginModelToolManifest> ModelTools);

    private readonly IReadOnlyDictionary<(string Id, string Version), Entry> _plugins;

    private TesseraPluginRegistry(
        IReadOnlyDictionary<(string Id, string Version), Entry> plugins,
        bool isAuthoritative)
    {
        _plugins = plugins;
        IsAuthoritative = isAuthoritative;
    }

    public static TesseraPluginRegistry Empty { get; } = new(
        new Dictionary<(string Id, string Version), Entry>(),
        false);

    public static TesseraPluginRegistry AuthoritativeEmpty { get; } = new(
        new Dictionary<(string Id, string Version), Entry>(),
        true);

    public bool IsAuthoritative { get; }

    internal static TesseraPluginRegistry Create(
        IEnumerable<(ITesseraCapabilityPlugin Plugin, TesseraPluginManifest Manifest)> plugins)
    {
        var entries = new Dictionary<(string Id, string Version), Entry>();
        foreach (var (plugin, manifest) in plugins)
        {
            var modelTools = SnapshotModelTools(plugin, manifest);
            if (!entries.TryAdd((manifest.PluginId, manifest.Version), new(plugin, manifest, modelTools)))
                throw new PluginModuleException("duplicate_module_identity");
        }

        return new(entries, true);
    }

    public bool TryResolve(string pluginId, string version, out ITesseraCapabilityPlugin? plugin)
    {
        if (_plugins.TryGetValue((pluginId, version), out var entry))
        {
            plugin = entry.Plugin;
            return true;
        }

        plugin = null;
        return false;
    }

    public ITesseraCapabilityPlugin Resolve(string pluginId, string version)
    {
        if (!TryResolve(pluginId, version, out var plugin))
        {
            throw new KeyNotFoundException($"Plugin {pluginId}/{version} is not registered.");
        }

        return plugin!;
    }

    public IReadOnlyList<TesseraPluginManifest> ListManifests()
        => _plugins.Values
            .Select(entry => entry.Manifest)
            .OrderBy(manifest => manifest.PluginId, StringComparer.Ordinal)
            .ThenBy(manifest => manifest.Version, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyList<ITesseraCapabilityPlugin> ListPlugins()
        => _plugins.Values
            .OrderBy(entry => entry.Manifest.PluginId, StringComparer.Ordinal)
            .ThenBy(entry => entry.Manifest.Version, StringComparer.Ordinal)
            .Select(entry => entry.Plugin)
            .ToArray();

    public async ValueTask<ICapability> CreateCapabilityAsync(
        string pluginId,
        string pluginVersion,
        string capabilityId,
        string capabilityVersion,
        PluginCapabilityContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_plugins.TryGetValue((pluginId, pluginVersion), out var entry))
            throw new PluginModuleException("plugin_module_unavailable");
        var capability = entry.Manifest.Capabilities.SingleOrDefault(item =>
            string.Equals(item.CapabilityId, capabilityId, StringComparison.Ordinal)
            && string.Equals(item.Version, capabilityVersion, StringComparison.Ordinal));
        if (capability is null) throw new PluginModuleException("capability_unavailable");
        if (capability.AccountRequired)
        {
            if (context.Account is null
                || !string.Equals(context.Account.PluginId, pluginId, StringComparison.Ordinal)
                || !string.Equals(context.Account.PluginVersion, pluginVersion, StringComparison.Ordinal)
                || context.Account.Lifecycle != AccountLifecycle.Connected
                || capability.RequiredPermissions.Any(permission =>
                    !context.Account.Permissions.Contains(permission, StringComparer.Ordinal)))
                throw new PluginModuleException("account_unavailable");
        }
        else if (context.Account is not null)
        {
            throw new PluginModuleException("account_not_allowed");
        }

        McpServerContract? discoveredMcp = null;
        if (entry.Plugin is ITesseraMcpPlugin mcpPlugin)
        {
            if (capability.SideEffectClass == SideEffectClass.ReadOnly)
            {
                discoveredMcp = await mcpPlugin.DiscoverMcpAsync(context, cancellationToken).ConfigureAwait(false);
                ValidateMcpCompatibility(mcpPlugin.RequiredMcpServer, mcpPlugin.RequiredMcpTools, discoveredMcp);
            }
        }

        var implementation = await entry.Plugin.CreateCapabilityAsync(
            capabilityId,
            capabilityVersion,
            context,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(implementation.Descriptor.CapabilityId, capabilityId, StringComparison.Ordinal)
            || !string.Equals(implementation.Descriptor.Version, capabilityVersion, StringComparison.Ordinal))
            throw new PluginModuleException("capability_implementation_mismatch");
        return entry.Plugin is ITesseraMcpPlugin validatedMcpPlugin
            ? new McpValidatedCapability(
                implementation,
                validatedMcpPlugin,
                context,
                capability.ExternalToolName,
                discoveredMcp)
            : implementation;
    }

    private sealed class McpValidatedCapability(
        ICapability implementation,
        ITesseraMcpPlugin plugin,
        PluginCapabilityContext context,
        string externalToolName,
        McpServerContract? discoveredContract) : ICapability
    {
        public CapabilityDescriptor Descriptor => implementation.Descriptor;

        public async ValueTask<CapabilityResult> InvokeAsync(
            CapabilityInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            var contract = discoveredContract
                ?? await plugin.DiscoverMcpAsync(context, cancellationToken).ConfigureAwait(false);
            ValidateMcpCompatibility(plugin.RequiredMcpServer, plugin.RequiredMcpTools, contract);
            var result = await implementation.InvokeAsync(invocation, cancellationToken).ConfigureAwait(false);
            return result with
            {
                RuntimeIdentity = new(
                    contract.ServerId,
                    contract.ServerName!,
                    contract.ServerVersion!,
                    externalToolName),
            };
        }
    }

    private static void ValidateMcpCompatibility(
        RequiredMcpServer server,
        IReadOnlyList<RequiredMcpTool> requirements,
        McpServerContract contract)
    {
        if (server is null
            || string.IsNullOrWhiteSpace(server.Name)
            || string.IsNullOrWhiteSpace(server.Version)
            || !string.Equals(contract.ServerName, server.Name, StringComparison.Ordinal)
            || !string.Equals(contract.ServerVersion, server.Version, StringComparison.Ordinal))
            throw new PluginModuleException("mcp_server_incompatible");
        if (requirements is null || requirements.Count == 0)
            throw new PluginModuleException("mcp_toolset_incompatible");
        if (contract.Tools.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count()
            != contract.Tools.Count)
            throw new PluginModuleException("mcp_toolset_incompatible");
        var requiredNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var requirement in requirements)
        {
            if (string.IsNullOrWhiteSpace(requirement.Name)
                || !requiredNames.Add(requirement.Name)
                || requirement.RequiredInputProperties is null
                || requirement.RequiredOutputProperties is null
                || requirement.RequiredInputProperties.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count()
                    != requirement.RequiredInputProperties.Count)
                throw new PluginModuleException("mcp_toolset_incompatible");
            if (requirement.RequiredOutputProperties.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count()
                != requirement.RequiredOutputProperties.Count)
                throw new PluginModuleException("mcp_toolset_incompatible");
            var tool = contract.Tools.SingleOrDefault(item => item.Name == requirement.Name)
                ?? throw new PluginModuleException("mcp_toolset_incompatible");
            if (!CompatibleSchema(tool.InputSchema, requirement.RequiredInputProperties)
                || tool.OutputSchema is not { } output
                || !CompatibleSchema(output, requirement.RequiredOutputProperties))
                throw new PluginModuleException("mcp_schema_incompatible");
        }
    }

    private static bool CompatibleSchema(
        JsonElement schema,
        IReadOnlyList<RequiredMcpProperty> requirements)
    {
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("type", out var rootType)
            || rootType.GetString() != "object"
            || !schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
            return false;
        var requiredNames = new HashSet<string>(StringComparer.Ordinal);
        if (requirements.Count > 0)
        {
            if (!schema.TryGetProperty("required", out var required)
                || required.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var item in required.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String
                    || item.GetString() is not { } name
                    || !requiredNames.Add(name))
                    return false;
            }
        }
        foreach (var requirement in requirements)
        {
            if (string.IsNullOrWhiteSpace(requirement.Name)
                || string.IsNullOrWhiteSpace(requirement.Type)
                || !properties.TryGetProperty(requirement.Name, out var property)
                || property.ValueKind != JsonValueKind.Object
                || !property.TryGetProperty("type", out var propertyType)
                || propertyType.ValueKind != JsonValueKind.String
                || !requiredNames.Contains(requirement.Name))
                return false;
            var actual = propertyType.GetString();
            if (!string.Equals(actual, requirement.Type, StringComparison.Ordinal)
                && !(requirement.Type == "integer" && actual == "number"))
                return false;
        }
        return true;
    }

    public IReadOnlyList<ProjectedModelTool> ProjectModelTools(
        IReadOnlyList<ConnectedAccount> accounts,
        IReadOnlySet<(string Id, string Version)> capabilityGrants,
        IReadOnlySet<string>? sideEffectGrants = null,
        bool forJob = false)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(capabilityGrants);
        var projected = new List<ProjectedModelTool>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in _plugins.Values.OrderBy(item => item.Manifest.PluginId, StringComparer.Ordinal))
        {
            foreach (var tool in entry.ModelTools.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                var capability = entry.Manifest.Capabilities.Single(item =>
                    item.CapabilityId == tool.CapabilityId && item.Version == tool.CapabilityVersion);
                if (!capabilityGrants.Contains((capability.CapabilityId, capability.Version))
                    || forJob && !tool.JobEligible
                    || forJob && capability.SideEffectClass != SideEffectClass.ReadOnly
                        && (sideEffectGrants is null || !sideEffectGrants.Contains(capability.SideEffectClass.ToString())))
                    continue;
                var eligible = capability.AccountRequired
                    ? accounts.Where(account =>
                        account.Lifecycle == AccountLifecycle.Connected
                        && account.PluginId == entry.Manifest.PluginId
                        && account.PluginVersion == entry.Manifest.Version
                        && capability.RequiredPermissions.All(permission => account.Permissions.Contains(permission, StringComparer.Ordinal))
                        && account.CapabilityBindings.Any(binding =>
                            binding.PluginId == entry.Manifest.PluginId
                            && binding.PluginVersion == entry.Manifest.Version
                            && binding.CapabilityId == capability.CapabilityId
                            && binding.CapabilityVersion == capability.Version))
                        .OrderBy(account => account.AccountId, StringComparer.Ordinal)
                        .ToArray()
                    : [];
                if (capability.AccountRequired && eligible.Length == 0) continue;
                if (!names.Add(tool.Name)) throw new PluginModuleException("duplicate_model_tool");
                projected.Add(new(
                    entry.Manifest.PluginId,
                    entry.Manifest.Version,
                    tool,
                    capability,
                    eligible,
                    ModelParameters(tool.InputSchema, eligible)));
            }
        }
        return projected;
    }

    public PluginModelToolBinding BindModelTool(ProjectedModelTool projected, JsonElement arguments)
    {
        ArgumentNullException.ThrowIfNull(projected);
        if (!_plugins.TryGetValue((projected.PluginId, projected.PluginVersion), out var entry)
            || entry.Plugin is not ITesseraModelToolPlugin modelPlugin
            || !entry.ModelTools.Any(item => item.Name == projected.Tool.Name
                && item.CapabilityId == projected.Tool.CapabilityId
                && item.CapabilityVersion == projected.Tool.CapabilityVersion))
            throw new PluginModuleException("tool_not_available");
        ConnectedAccount? account = null;
        if (projected.Capability.AccountRequired)
        {
            var requested = arguments.TryGetProperty("accountId", out var accountValue)
                && accountValue.ValueKind == JsonValueKind.String
                ? accountValue.GetString()
                : null;
            account = projected.Accounts.Count == 1 && requested is null
                ? projected.Accounts[0]
                : projected.Accounts.SingleOrDefault(item => item.AccountId == requested);
            if (account is null) throw new PluginModuleException("account_ambiguous");
        }
        var binding = modelPlugin.BindModelTool(projected.Tool.Name, arguments, account);
        if (binding.AccountId != account?.AccountId)
            throw new PluginModuleException("account_substitution_denied");
        return binding;
    }

    public PluginAccountDefinition DefineAccount(
        string pluginId,
        string pluginVersion,
        JsonElement nonSecretConfiguration)
    {
        if (!_plugins.TryGetValue((pluginId, pluginVersion), out var entry)
            || entry.Plugin is not ITesseraAccountPlugin accountPlugin)
            throw new PluginModuleException("account_provider_unavailable");
        var definition = accountPlugin.DefineAccount(pluginVersion, nonSecretConfiguration);
        if (string.IsNullOrWhiteSpace(definition.ProviderId)
            || definition.CapabilityBindings.Any(binding =>
                binding.PluginId != pluginId
                || binding.PluginVersion != pluginVersion
                || !entry.Manifest.Capabilities.Any(capability =>
                    capability.CapabilityId == binding.CapabilityId
                    && capability.Version == binding.CapabilityVersion)))
            throw new PluginModuleException("invalid_account_definition");
        return definition;
    }

    public async ValueTask<PluginAccountValidation> ValidateAccountAsync(
        ConnectedAccount account,
        PluginCapabilityContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_plugins.TryGetValue((account.PluginId, account.PluginVersion), out var entry)
            || entry.Plugin is not ITesseraAccountPlugin accountPlugin)
            throw new PluginModuleException("account_provider_unavailable");
        return await accountPlugin.ValidateAccountAsync(
            account,
            context.AccountCredential,
            context,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisconnectAccountAsync(
        ConnectedAccount account,
        PluginCapabilityContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_plugins.TryGetValue((account.PluginId, account.PluginVersion), out var entry)
            || entry.Plugin is not ITesseraAccountPlugin accountPlugin)
            return;
        await accountPlugin.DisconnectAccountAsync(
            account,
            context.AccountCredential,
            context,
            cancellationToken).ConfigureAwait(false);
    }

    private static PluginModelToolManifest[] SnapshotModelTools(
        ITesseraCapabilityPlugin plugin,
        TesseraPluginManifest manifest)
    {
        if (plugin is not ITesseraModelToolPlugin modelPlugin) return [];
        var names = new HashSet<string>(StringComparer.Ordinal);
        var values = new List<PluginModelToolManifest>();
        foreach (var tool in modelPlugin.ModelTools)
        {
            if (string.IsNullOrWhiteSpace(tool.Name)
                || tool.Name.Length > 256
                || tool.Name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.'))
                || !names.Add(tool.Name)
                || string.IsNullOrWhiteSpace(tool.Description)
                || tool.Description.Length > 1024
                || tool.InputSchema.ValueKind != JsonValueKind.Object
                || JsonSerializer.SerializeToUtf8Bytes(tool.InputSchema).Length > 64 * 1024
                || !manifest.Capabilities.Any(item =>
                    item.CapabilityId == tool.CapabilityId && item.Version == tool.CapabilityVersion)
                || (tool.ProposalCapabilityId is null) != (tool.ProposalCapabilityVersion is null)
                || tool.ProposalCapabilityId is not null && !manifest.Capabilities.Any(item =>
                    item.CapabilityId == tool.ProposalCapabilityId
                    && item.Version == tool.ProposalCapabilityVersion
                    && item.SideEffectClass == SideEffectClass.ReadOnly))
                throw new PluginModuleException("invalid_model_tool");
            values.Add(tool with { InputSchema = tool.InputSchema.Clone() });
        }
        return values.ToArray();
    }

    private static JsonElement ModelParameters(
        JsonElement schema,
        ConnectedAccount[] accounts)
    {
        var root = JsonNode.Parse(schema.GetRawText())?.AsObject()
            ?? throw new PluginModuleException("invalid_model_tool");
        var properties = root["properties"] as JsonObject ?? new JsonObject();
        root["properties"] = properties;
        if (accounts.Length > 0)
        {
            properties["accountId"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(accounts.Select(item => JsonValue.Create(item.AccountId)).ToArray()),
            };
            if (accounts.Length > 1)
            {
                var required = root["required"] as JsonArray ?? new JsonArray();
                root["required"] = required;
                if (!required.Any(item => item?.GetValue<string>() == "accountId")) required.Add("accountId");
            }
        }
        root["additionalProperties"] = false;
        return JsonSerializer.SerializeToElement(root);
    }

}