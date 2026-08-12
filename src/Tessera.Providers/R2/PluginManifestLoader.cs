using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Tessera.Providers.R2;

public sealed record PluginCapabilityManifest(
    string Id,
    string Version,
    string Description,
    string ExecutorKind,
    bool AccountRequired,
    string[] RequiredPermissions,
    string SideEffectClass,
    int TimeoutMilliseconds,
    int MaxResultBytes);

public sealed record PluginManifest(
    string Id,
    string Version,
    string Name,
    string Publisher,
    string MinimumTesseraVersion,
    PluginCapabilityManifest[] Capabilities,
    string[]? ConfigurationFields = null);

public sealed record ValidatedPluginPackage(PluginManifest Manifest, string PackageHash, string ManifestPath);

public sealed class PluginManifestException(string message) : Exception(message);

public static partial class PluginManifestLoader
{
    private const int MaximumManifestBytes = 256 * 1024;
    private const string CurrentTesseraVersion = "2.0.0";
    private static readonly HashSet<string> ExecutorKinds = ["native", "mcp"];
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static ValidatedPluginPackage Load(
        string root,
        string packageDirectory,
        IReadOnlyDictionary<string, string> catalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        var canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var canonicalPackage = Path.GetFullPath(Path.Combine(canonicalRoot, packageDirectory));
        if (!canonicalPackage.StartsWith(canonicalRoot, StringComparison.Ordinal))
            throw new PluginManifestException("Plugin package escapes the configured root.");
        for (var directory = new DirectoryInfo(canonicalPackage); directory is not null
             && directory.FullName.StartsWith(canonicalRoot, StringComparison.Ordinal); directory = directory.Parent)
        {
            if (directory.LinkTarget is not null)
                throw new PluginManifestException("Plugin package path cannot contain symbolic links.");
        }
        var manifestPath = Path.Combine(canonicalPackage, "manifest.json");
        if (Directory.EnumerateFileSystemEntries(canonicalPackage).Any(entry =>
                !string.Equals(Path.GetFullPath(entry), manifestPath, StringComparison.Ordinal)))
            throw new PluginManifestException("Declarative plugin packages may contain only manifest.json.");
        var info = new FileInfo(manifestPath);
        if (!info.Exists || info.LinkTarget is not null || info.Length is <= 0 or > MaximumManifestBytes)
            throw new PluginManifestException("Plugin manifest must be a bounded regular non-symlink file.");
        var bytes = File.ReadAllBytes(manifestPath);
        info.Refresh();
        if (!info.Exists || info.LinkTarget is not null || info.Length != bytes.Length)
            throw new PluginManifestException("Plugin manifest changed or became a symlink while it was read.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        PluginManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PluginManifest>(bytes, SerializerOptions)
                ?? throw new PluginManifestException("Plugin manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new PluginManifestException($"Plugin manifest JSON is invalid: {exception.Path ?? "unknown field"}.");
        }
        Validate(manifest);
        var key = $"{manifest.Id}@{manifest.Version}";
        if (!catalog.TryGetValue(key, out var expected) || !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expected), Convert.FromHexString(hash)))
            throw new PluginManifestException("Plugin package hash does not match the operator catalog.");
        return new(manifest, hash, manifestPath);
    }

    private static void Validate(PluginManifest manifest)
    {
        if (!Identifier().IsMatch(manifest.Id) || !SemanticVersion().IsMatch(manifest.Version)
            || !SemanticVersion().IsMatch(manifest.MinimumTesseraVersion)
            || string.IsNullOrWhiteSpace(manifest.Name) || string.IsNullOrWhiteSpace(manifest.Publisher))
            throw new PluginManifestException("Plugin identity, version, name, or publisher is invalid.");
        if (CompareVersion(manifest.MinimumTesseraVersion, CurrentTesseraVersion) > 0)
            throw new PluginManifestException("Plugin requires a newer Tessera version.");
        if (manifest.Capabilities is null || manifest.Capabilities.Length == 0
            || manifest.Capabilities.Select(item => $"{item.Id}@{item.Version}").Distinct(StringComparer.Ordinal).Count() != manifest.Capabilities.Length)
            throw new PluginManifestException("Plugin capabilities must be present and unique.");
        foreach (var capability in manifest.Capabilities)
        {
            if (!CapabilityIdentifier().IsMatch(capability.Id) || !CapabilityVersion().IsMatch(capability.Version)
                || !ExecutorKinds.Contains(capability.ExecutorKind)
                || capability.TimeoutMilliseconds is < 100 or > 120_000
                || capability.MaxResultBytes is < 1 or > 1_048_576)
                throw new PluginManifestException("Plugin capability declaration is invalid.");
        }
        if (manifest.ConfigurationFields is { } fields
            && (fields.Any(string.IsNullOrWhiteSpace)
                || fields.Distinct(StringComparer.Ordinal).Count() != fields.Length
                || fields.Any(IsSecretLike)))
            throw new PluginManifestException("Plugin configuration fields must be unique non-secret names.");
    }

    private static bool IsSecretLike(string field)
    {var value=field.ToLowerInvariant();return value.Contains("secret",StringComparison.Ordinal)||value.Contains("token",StringComparison.Ordinal)||value.Contains("password",StringComparison.Ordinal)||value.Contains("credential",StringComparison.Ordinal)||value.EndsWith("key",StringComparison.Ordinal);}

    private static int CompareVersion(string left,string right)
    {
        var leftParts=left.Split('-',2)[0].Split('.').Select(int.Parse).ToArray();
        var rightParts=right.Split('-',2)[0].Split('.').Select(int.Parse).ToArray();
        for(var index=0;index<3;index++){var comparison=leftParts[index].CompareTo(rightParts[index]);if(comparison!=0)return comparison;}
        return 0;
    }

    [GeneratedRegex("^[a-z][a-z0-9.-]{1,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    [GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticVersion();

    [GeneratedRegex("^(?:0|[1-9][0-9]*)(?:\\.(?:0|[1-9][0-9]*)){0,2}(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex CapabilityVersion();

    [GeneratedRegex("^[a-z][a-z0-9._-]{1,255}$", RegexOptions.CultureInvariant)]
    private static partial Regex CapabilityIdentifier();
}