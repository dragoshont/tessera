using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tessera.Core.Product;
using Tessera.Core.Kernel;
using Tessera.Persistence.Sqlite;
using Tessera.Plugin.Abstractions;
using Tessera.Providers.R2;

namespace Tessera.Broker;

internal sealed class R2PluginCatalog
{
    private readonly IReadOnlyList<ValidatedPluginPackage> _packages;

    public R2PluginCatalog(string root, string catalogPath)
    {
        var catalog = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(catalogPath))
            ?? throw new PluginManifestException("Plugin catalog is empty.");
        _packages = catalog.Keys
            .Select(key => key.Split('@', 2)[0])
            .Distinct(StringComparer.Ordinal)
            .Select(packageDirectory => PluginManifestLoader.Load(root, packageDirectory, catalog))
            .OrderBy(package => package.Manifest.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public void ValidateExecutableModules(TesseraPluginRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        foreach (var module in registry.ListManifests())
        {
            var package = _packages.SingleOrDefault(item =>
                item.Manifest.Id == module.PluginId && item.Manifest.Version == module.Version)
                ?? throw new PluginModuleException("module_package_missing");
            foreach (var capability in module.Capabilities)
            {
                var declaration = package.Manifest.Capabilities.SingleOrDefault(item =>
                    item.Id == capability.CapabilityId && item.Version == capability.Version)
                    ?? throw new PluginModuleException("module_capability_undeclared");
                if (!Enum.TryParse<SideEffectClass>(declaration.SideEffectClass, ignoreCase: false, out var sideEffect)
                    || sideEffect != capability.SideEffectClass
                    || declaration.AccountRequired != capability.AccountRequired
                    || !declaration.RequiredPermissions.Order(StringComparer.Ordinal)
                        .SequenceEqual(capability.RequiredPermissions.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                    throw new PluginModuleException("module_package_mismatch");
            }
        }
    }

    public async Task EnsureInstalledAsync(
        SqliteKernelStore store,
        string ownerPrincipalId,
        CancellationToken cancellationToken)
    {
        var installed = await store.ListPluginInstallationsAsync(ownerPrincipalId, cancellationToken)
            .ConfigureAwait(false);
        var existing = installed
            .Select(plugin => $"{plugin.PluginId}@{plugin.PluginVersion}")
            .ToHashSet(StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        foreach (var package in _packages)
        {
            var key = $"{package.Manifest.Id}@{package.Manifest.Version}";
            if (existing.Contains(key)) continue;
            try
            {
                await store.AddPluginInstallationAsync(new(
                    ownerPrincipalId,
                    package.Manifest.Id,
                    package.Manifest.Version,
                    package.Manifest.Name,
                    package.Manifest.Publisher,
                    package.PackageHash,
                    JsonSerializer.Serialize(package.Manifest),
                    "{}",
                    true,
                    now,
                    now,
                    1), cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                if (await store.GetPluginInstallationAsync(
                    ownerPrincipalId,
                    package.Manifest.Id,
                    package.Manifest.Version,
                    cancellationToken).ConfigureAwait(false) is null)
                {
                    throw;
                }
            }
        }
    }
}
