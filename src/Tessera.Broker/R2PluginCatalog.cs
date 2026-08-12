using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Tessera.Core.Product;
using Tessera.Core.Kernel;
using Tessera.Persistence.Sqlite;
using Tessera.Plugin.Abstractions;
using Tessera.Providers.R2;

namespace Tessera.Broker;

internal sealed class R2PluginCatalog : IDisposable
{
    private readonly IReadOnlyList<ValidatedPluginPackage> _packages;
    private readonly SemaphoreSlim _installGate = new(1, 1);

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

    public IReadOnlyList<ValidatedPluginPackage> ListPackages() => _packages;

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

    public async Task<ReviewedPluginInstallResult> InstallIdempotentAsync(
        SqliteKernelStore store,
        string ownerPrincipalId,
        string idempotencyKey,
        string pluginId,
        string pluginVersion,
        CancellationToken cancellationToken)
    {
        const string routeFamily = "integration-install";
        var requestHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{pluginId}\n{pluginVersion}")));
        await _installGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var prior = await store.GetIdempotencyReceiptAsync(
                    ownerPrincipalId,
                    routeFamily,
                    idempotencyKey,
                    cancellationToken)
                .ConfigureAwait(false);
            if (prior is not null)
                return Replay(prior, requestHash);
            var package = _packages.SingleOrDefault(item =>
                item.Manifest.Id == pluginId && item.Manifest.Version == pluginVersion)
                ?? throw new KeyNotFoundException("reviewed_package_not_found");
            var body = JsonSerializer.Serialize(new
            {
                pluginId,
                version = pluginVersion,
                installState = "INSTALLED",
            });
            var now = DateTimeOffset.UtcNow;
            var installation = new PluginInstallation(
                ownerPrincipalId,
                package.Manifest.Id,
                package.Manifest.Version,
                package.Manifest.Name,
                package.Manifest.Publisher,
                package.PackageHash,
                JsonSerializer.Serialize(package.Manifest),
                "{}",
                false,
                now,
                now,
                1);
            var receipt = new ProductIdempotencyReceipt(
                ownerPrincipalId,
                routeFamily,
                idempotencyKey,
                requestHash,
                StatusCodes.Status200OK,
                body,
                "plugin-installation",
                $"{pluginId}@{pluginVersion}",
                now);
            var result = await store.CommitPluginInstallWithReceiptAsync(
                    installation,
                    receipt,
                    cancellationToken)
                .ConfigureAwait(false);
            return Replay(result.Receipt, requestHash) with { Replayed = !result.Created };
        }
        finally
        {
            _installGate.Release();
        }
    }

    private static ReviewedPluginInstallResult Replay(
        ProductIdempotencyReceipt receipt,
        string requestHash)
    {
        if (!string.Equals(receipt.RequestHash, requestHash, StringComparison.Ordinal))
            throw new InvalidOperationException("idempotency_conflict");
        return new(receipt.ResponseBodyJson, receipt.ResponseStatus, true);
    }

    public void Dispose() => _installGate.Dispose();
}

internal sealed record ReviewedPluginInstallResult(
    string ResponseBodyJson,
    int StatusCode,
    bool Replayed);
