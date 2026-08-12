using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Tessera.Persistence.Sqlite;
using Xunit;

namespace Tessera.Broker.Tests;

public sealed class R2PluginCatalogTests
{
    [Fact]
    public async Task Reviewed_package_install_is_disabled_durable_keyed_and_owner_scoped()
    {
        var fixture = await CatalogFixture.CreateAsync();
        try
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                fixture.Catalog.InstallIdempotentAsync(
                    fixture.Store,
                    fixture.OwnerA.PrincipalId,
                    "install-key",
                    "local",
                    "1.0.0",
                    default)));

            Assert.Single(results, result => !result.Replayed);
            Assert.Equal(7, results.Count(result => result.Replayed));
            Assert.Single(results.Select(result => result.ResponseBodyJson).Distinct(StringComparer.Ordinal));
            var first = Assert.Single(await fixture.Store.ListPluginInstallationsAsync(fixture.OwnerA.PrincipalId));
            Assert.False(first.Enabled);
            Assert.Empty(await fixture.Store.ListPluginInstallationsAsync(fixture.OwnerB.PrincipalId));
            Assert.Equal(fixture.PackageHash, first.PackageHash);
            Assert.Contains("local.time", first.ManifestJson, StringComparison.Ordinal);

            var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Catalog.InstallIdempotentAsync(
                fixture.Store,
                fixture.OwnerA.PrincipalId,
                "install-key",
                "unknown",
                "1.0.0",
                default));
            Assert.Equal("idempotency_conflict", conflict.Message);
            Assert.Empty(await fixture.Store.ListPluginInstallationsAsync(fixture.OwnerB.PrincipalId));

            var missing = await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Catalog.InstallIdempotentAsync(
                fixture.Store,
                fixture.OwnerA.PrincipalId,
                "missing-key",
                "unknown",
                "1.0.0",
                default));
            Assert.Equal("reviewed_package_not_found", missing.Message);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task Install_and_receipt_roll_back_together_when_receipt_commit_fails()
    {
        var fixture = await CatalogFixture.CreateAsync();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var installation = new PluginInstallation(
                fixture.OwnerA.PrincipalId,
                "local",
                "1.0.0",
                "Local",
                "Tessera",
                fixture.PackageHash,
                "{}",
                "{}",
                false,
                now,
                now,
                1);
            var invalidReceipt = new ProductIdempotencyReceipt(
                fixture.OwnerA.PrincipalId,
                "integration-install",
                "fault-key",
                "request-hash",
                99,
                "{}",
                "plugin-installation",
                "local@1.0.0",
                now);

            await Assert.ThrowsAsync<SqliteException>(() => fixture.Store
                .CommitPluginInstallWithReceiptAsync(installation, invalidReceipt));

            Assert.Empty(await fixture.Store.ListPluginInstallationsAsync(fixture.OwnerA.PrincipalId));
            Assert.Null(await fixture.Store.GetIdempotencyReceiptAsync(
                fixture.OwnerA.PrincipalId,
                "integration-install",
                "fault-key"));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private sealed record CatalogFixture(
        string Directory,
        SqliteKernelStore Store,
        PrincipalRef OwnerA,
        PrincipalRef OwnerB,
        R2PluginCatalog Catalog,
        string PackageHash) : IDisposable
    {
        public static async Task<CatalogFixture> CreateAsync()
        {
            var directory = System.IO.Directory.CreateTempSubdirectory("tessera-plugin-install-test").FullName;
            var package = Path.Combine(directory, "local");
            System.IO.Directory.CreateDirectory(package);
            var manifest = """{"Id":"local","Version":"1.0.0","Name":"Local","Publisher":"Tessera","MinimumTesseraVersion":"2.0.0","Capabilities":[{"Id":"local.time","Version":"1.0.0","Description":"Time","ExecutorKind":"native","AccountRequired":false,"RequiredPermissions":[],"SideEffectClass":"ReadOnly","TimeoutMilliseconds":1000,"MaxResultBytes":4096}]}""";
            var bytes = Encoding.UTF8.GetBytes(manifest);
            await File.WriteAllBytesAsync(Path.Combine(package, "manifest.json"), bytes);
            var packageHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var catalogPath = Path.Combine(directory, "catalog.json");
            await File.WriteAllTextAsync(catalogPath, JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["local@1.0.0"] = packageHash,
            }));
            var store = new SqliteKernelStore(Path.Combine(directory, "product.db"));
            await store.InitializeAsync();
            var ownerA = PrincipalRef.Create("https://tessera.test", "tenant", "install-owner-a", null, DateTimeOffset.UtcNow);
            var ownerB = PrincipalRef.Create("https://tessera.test", "tenant", "install-owner-b", null, DateTimeOffset.UtcNow);
            await store.AddAsync(ownerA);
            await store.AddAsync(ownerB);
            return new(directory, store, ownerA, ownerB, new R2PluginCatalog(directory, catalogPath), packageHash);
        }

        public void Dispose()
        {
            Catalog.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
