using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Tessera.Plugin.Abstractions;

#pragma warning disable CA1707 // Trust-state names are fixed by the accepted plugin lifecycle contract.
public enum PluginTrustState
{
    BUILT_IN,
    TRUSTED_EXTERNAL,
    USER_APPROVED_EXTERNAL,
    UNTRUSTED,
    DISABLED,
}
#pragma warning restore CA1707

public sealed record PluginModuleInstallation(
    string PluginId,
    string Version,
    string AssemblyFileName,
    string AssemblySha256,
    PluginTrustState TrustState,
    IReadOnlyList<PluginCapabilityManifest> Capabilities);

public sealed record PluginModuleArtifact(
    string PluginId,
    string Version,
    string AssemblyFileName,
    string AssemblySha256,
    PluginTrustState TrustState);

public sealed class PluginModuleException(string errorCode, Exception? innerException = null)
    : Exception(errorCode, innerException)
{
    public string ErrorCode { get; } = errorCode;
}

public static partial class PluginModuleDiscovery
{
    public const int MaximumModules = 64;
    public const int MaximumCapabilitiesPerModule = 256;
    private const int MaximumAssemblyBytes = 64 * 1024 * 1024;
    private const int MaximumSchemaBytes = 64 * 1024;
    private static readonly JsonSerializerOptions ArtifactCatalogOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    public static TesseraPluginRegistry Discover(
        string root,
        IReadOnlyList<PluginModuleInstallation> installations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(installations);
        if (installations.Count > MaximumModules)
            throw new PluginModuleException("module_bound_exceeded");

        var canonicalRoot = Path.GetFullPath(root);
        var candidates = ValidateInstallations(installations);
        if (!Directory.Exists(canonicalRoot)) return TesseraPluginRegistry.Create([]);
        if (new DirectoryInfo(canonicalRoot).LinkTarget is not null)
            throw new PluginModuleException("module_root_symlink");

        var modules = new List<(ITesseraCapabilityPlugin Plugin, TesseraPluginManifest Manifest)>();
        var moduleIdentities = new HashSet<(string Id, string Version)>();
        foreach (var installation in candidates)
        {
            if (!CanExecute(installation.TrustState)) continue;
            var assemblyPath = Path.Combine(canonicalRoot, installation.AssemblyFileName);
            if (!File.Exists(assemblyPath)) continue;

            var module = LoadModule(assemblyPath, installation.AssemblySha256);
            var manifest = ValidateAndSnapshot(module.Manifest);
            if (!string.Equals(manifest.PluginId, installation.PluginId, StringComparison.Ordinal)
                || !string.Equals(manifest.Version, installation.Version, StringComparison.Ordinal))
                throw new PluginModuleException("module_identity_mismatch");
            if (!moduleIdentities.Add((manifest.PluginId, manifest.Version)))
                throw new PluginModuleException("duplicate_module_identity");
            ValidateCapabilities(manifest.Capabilities, installation.Capabilities);
            modules.Add((module, manifest));
        }

        return TesseraPluginRegistry.Create(modules);
    }

    public static TesseraPluginRegistry DiscoverArtifacts(
        string root,
        IReadOnlyList<PluginModuleArtifact> artifacts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(artifacts);
        if (artifacts.Count > MaximumModules) throw new PluginModuleException("module_bound_exceeded");
        var canonicalRoot = Path.GetFullPath(root);
        if (!Directory.Exists(canonicalRoot)) return TesseraPluginRegistry.Create([]);
        if (new DirectoryInfo(canonicalRoot).LinkTarget is not null)
            throw new PluginModuleException("module_root_symlink");
        var identities = new HashSet<(string Id, string Version)>();
        var fileNames = new HashSet<string>(StringComparer.Ordinal);
        var modules = new List<(ITesseraCapabilityPlugin Plugin, TesseraPluginManifest Manifest)>();
        foreach (var artifact in artifacts.OrderBy(item => item.AssemblyFileName, StringComparer.Ordinal))
        {
            ValidateArtifact(artifact, identities, fileNames);
            if (!CanExecute(artifact.TrustState)) continue;
            var assemblyPath = Path.Combine(canonicalRoot, artifact.AssemblyFileName);
            if (!File.Exists(assemblyPath)) continue;
            var module = LoadModule(assemblyPath, artifact.AssemblySha256);
            var manifest = ValidateAndSnapshot(module.Manifest);
            if (manifest.PluginId != artifact.PluginId || manifest.Version != artifact.Version)
                throw new PluginModuleException("module_identity_mismatch");
            modules.Add((module, manifest));
        }
        return TesseraPluginRegistry.Create(modules);
    }

    public static IReadOnlyList<PluginModuleArtifact> LoadArtifactCatalog(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = new FileInfo(path);
        if (!file.Exists || file.LinkTarget is not null || file.Length is <= 0 or > 256 * 1024)
            throw new PluginModuleException("invalid_module_catalog");
        try
        {
            var bytes = File.ReadAllBytes(path);
            file.Refresh();
            if (!file.Exists || file.LinkTarget is not null || file.Length != bytes.Length)
                throw new PluginModuleException("module_catalog_changed");
            return JsonSerializer.Deserialize<PluginModuleArtifact[]>(bytes, ArtifactCatalogOptions)
                ?? throw new PluginModuleException("invalid_module_catalog");
        }
        catch (JsonException exception)
        {
            throw new PluginModuleException("invalid_module_catalog", exception);
        }
    }

    private static PluginModuleInstallation[] ValidateInstallations(
        IReadOnlyList<PluginModuleInstallation> installations)
    {
        var identities = new HashSet<(string Id, string Version)>();
        var fileNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var installation in installations)
        {
            if (!Identifier().IsMatch(installation.PluginId)
                || !SemanticVersion().IsMatch(installation.Version))
                throw new PluginModuleException("invalid_module_identity");
            if (!Enum.IsDefined(installation.TrustState))
                throw new PluginModuleException("invalid_module_trust");
            if (string.IsNullOrWhiteSpace(installation.AssemblyFileName)
                || installation.AssemblyFileName.Length > 256
                || !string.Equals(Path.GetFileName(installation.AssemblyFileName), installation.AssemblyFileName, StringComparison.Ordinal)
                || !installation.AssemblyFileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                throw new PluginModuleException("invalid_module_path");
            if (!Sha256().IsMatch(installation.AssemblySha256))
                throw new PluginModuleException("invalid_module_hash");
            if (!identities.Add((installation.PluginId, installation.Version)))
                throw new PluginModuleException("duplicate_installation_identity");
            if (!fileNames.Add(installation.AssemblyFileName))
                throw new PluginModuleException("duplicate_module_path");
            ValidateCapabilitySet(installation.Capabilities);
        }

        return installations
            .OrderBy(item => item.AssemblyFileName, StringComparer.Ordinal)
            .ToArray();
    }

    private static void ValidateArtifact(
        PluginModuleArtifact artifact,
        HashSet<(string Id, string Version)> identities,
        HashSet<string> fileNames)
    {
        if (!Identifier().IsMatch(artifact.PluginId) || !SemanticVersion().IsMatch(artifact.Version))
            throw new PluginModuleException("invalid_module_identity");
        if (!Enum.IsDefined(artifact.TrustState)) throw new PluginModuleException("invalid_module_trust");
        if (string.IsNullOrWhiteSpace(artifact.AssemblyFileName)
            || artifact.AssemblyFileName.Length > 256
            || Path.GetFileName(artifact.AssemblyFileName) != artifact.AssemblyFileName
            || !artifact.AssemblyFileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            throw new PluginModuleException("invalid_module_path");
        if (!Sha256().IsMatch(artifact.AssemblySha256)) throw new PluginModuleException("invalid_module_hash");
        if (!identities.Add((artifact.PluginId, artifact.Version))) throw new PluginModuleException("duplicate_installation_identity");
        if (!fileNames.Add(artifact.AssemblyFileName)) throw new PluginModuleException("duplicate_module_path");
    }

    private static ITesseraCapabilityPlugin LoadModule(string assemblyPath, string expectedHash)
    {
        var file = new FileInfo(assemblyPath);
        if (!file.Exists || file.LinkTarget is not null || file.Length is <= 0 or > MaximumAssemblyBytes)
            throw new PluginModuleException("invalid_module_file");

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(assemblyPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PluginModuleException("module_read_failed", exception);
        }

        file.Refresh();
        if (!file.Exists || file.LinkTarget is not null || file.Length != bytes.Length)
            throw new PluginModuleException("module_file_changed");
        var actualHash = SHA256.HashData(bytes);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, Convert.FromHexString(expectedHash)))
            throw new PluginModuleException("module_hash_mismatch");

        try
        {
            var assembly = Assembly.Load(bytes);
            var moduleTypes = assembly.GetTypes()
                .Where(type => typeof(ITesseraCapabilityPlugin).IsAssignableFrom(type)
                    && !type.IsAbstract
                    && (type.IsPublic || type.IsNestedPublic))
                .ToArray();
            if (moduleTypes.Length != 1)
                throw new PluginModuleException("malformed_module");
            return Activator.CreateInstance(moduleTypes[0]) as ITesseraCapabilityPlugin
                ?? throw new PluginModuleException("malformed_module");
        }
        catch (PluginModuleException)
        {
            throw;
        }
        catch (Exception exception) when (exception is BadImageFormatException
            or FileLoadException
            or ReflectionTypeLoadException
            or MissingMethodException
            or TargetInvocationException)
        {
            throw new PluginModuleException("malformed_module", exception);
        }
    }

    private static TesseraPluginManifest ValidateAndSnapshot(TesseraPluginManifest manifest)
    {
        if (manifest is null
            || !Identifier().IsMatch(manifest.PluginId)
            || !SemanticVersion().IsMatch(manifest.Version)
            || string.IsNullOrWhiteSpace(manifest.DisplayName)
            || manifest.DisplayName.Length > 256
            || string.IsNullOrWhiteSpace(manifest.ProviderId)
            || manifest.ProviderId.Length > 128)
            throw new PluginModuleException("invalid_module_manifest");
        ValidateCapabilitySet(manifest.Capabilities);
        return manifest with
        {
            Capabilities = Array.AsReadOnly(manifest.Capabilities
                .OrderBy(item => item.CapabilityId, StringComparer.Ordinal)
                .ThenBy(item => item.Version, StringComparer.Ordinal)
                .Select(Snapshot)
                .ToArray()),
        };
    }

    private static void ValidateCapabilities(
        IReadOnlyList<PluginCapabilityManifest> actual,
        IReadOnlyList<PluginCapabilityManifest> expected)
    {
        var expectedByIdentity = expected.ToDictionary(
            item => (item.CapabilityId, item.Version),
            item => item);
        foreach (var capability in actual)
        {
            if (!expectedByIdentity.TryGetValue((capability.CapabilityId, capability.Version), out var declaration)
                || !Equivalent(capability, declaration))
                throw new PluginModuleException("module_capability_mismatch");
        }
    }

    private static void ValidateCapabilitySet(IReadOnlyList<PluginCapabilityManifest> capabilities)
    {
        if (capabilities is null || capabilities.Count == 0 || capabilities.Count > MaximumCapabilitiesPerModule)
            throw new PluginModuleException("invalid_module_capabilities");
        var identities = new HashSet<(string Id, string Version)>();
        foreach (var capability in capabilities)
        {
            if (!CapabilityIdentifier().IsMatch(capability.CapabilityId)
                || !CapabilityVersion().IsMatch(capability.Version)
                || string.IsNullOrWhiteSpace(capability.Description)
                || capability.Description.Length > 1024
                || !ToolName().IsMatch(capability.ExternalToolName)
                || !identities.Add((capability.CapabilityId, capability.Version))
                || capability.RequiredPermissions is null
                || capability.AllowedDataClasses is null
                || capability.RequiredPermissions.Count > 64
                || capability.AllowedDataClasses.Count > 16
                || capability.RequiredPermissions.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > 256)
                || capability.RequiredPermissions.Distinct(StringComparer.Ordinal).Count() != capability.RequiredPermissions.Count
                || capability.AllowedDataClasses.Distinct().Count() != capability.AllowedDataClasses.Count)
                throw new PluginModuleException("invalid_module_capability");
            ValidateSchema(capability.InputSchema);
            ValidateSchema(capability.OutputSchema);
        }
    }

    private static void ValidateSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(type.GetString())
            || JsonSerializer.SerializeToUtf8Bytes(schema).Length > MaximumSchemaBytes)
            throw new PluginModuleException("invalid_tool_schema");
    }

    private static PluginCapabilityManifest Snapshot(PluginCapabilityManifest capability)
        => capability with
        {
            InputSchema = capability.InputSchema.Clone(),
            OutputSchema = capability.OutputSchema.Clone(),
            RequiredPermissions = Array.AsReadOnly(capability.RequiredPermissions.Order(StringComparer.Ordinal).ToArray()),
            AllowedDataClasses = Array.AsReadOnly(capability.AllowedDataClasses.Distinct().Order().ToArray()),
        };

    private static bool Equivalent(PluginCapabilityManifest left, PluginCapabilityManifest right)
        => string.Equals(left.CapabilityId, right.CapabilityId, StringComparison.Ordinal)
            && string.Equals(left.Version, right.Version, StringComparison.Ordinal)
            && string.Equals(left.Description, right.Description, StringComparison.Ordinal)
            && string.Equals(left.ExternalToolName, right.ExternalToolName, StringComparison.Ordinal)
            && JsonElement.DeepEquals(left.InputSchema, right.InputSchema)
            && JsonElement.DeepEquals(left.OutputSchema, right.OutputSchema)
            && left.SideEffectClass == right.SideEffectClass
            && left.AccountRequired == right.AccountRequired
            && left.RequiredPermissions.SequenceEqual(right.RequiredPermissions, StringComparer.Ordinal)
            && left.AllowedDataClasses.SequenceEqual(right.AllowedDataClasses)
            && left.IdempotencySupport == right.IdempotencySupport
            && left.VerificationSupport == right.VerificationSupport;

    private static bool CanExecute(PluginTrustState trustState)
        => trustState is PluginTrustState.BUILT_IN
            or PluginTrustState.TRUSTED_EXTERNAL
            or PluginTrustState.USER_APPROVED_EXTERNAL;

    [GeneratedRegex("^[a-z][a-z0-9.-]{1,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    [GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersion();

    [GeneratedRegex("^(?:0|[1-9][0-9]*)(?:\\.(?:0|[1-9][0-9]*)){0,2}(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex CapabilityVersion();

    [GeneratedRegex("^[a-z][a-z0-9._-]{1,255}$", RegexOptions.CultureInvariant)]
    private static partial Regex CapabilityIdentifier();

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,256}$", RegexOptions.CultureInvariant)]
    private static partial Regex ToolName();

    [GeneratedRegex("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256();
}