using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tessera.Core.Kernel;
using Tessera.Persistence.Sqlite;
using Xunit;

namespace Tessera.Broker.Tests;

public sealed class R2PluginCatalogTests
{
    [Fact]
    public async Task Pinned_catalog_materializes_each_plugin_once_per_owner()
    {
        var directory = Directory.CreateTempSubdirectory("tessera-plugin-catalog-test").FullName;
        try
        {
            var package = Path.Combine(directory, "local");
            Directory.CreateDirectory(package);
            var manifest = """{"Id":"local","Version":"1.0.0","Name":"Local","Publisher":"Tessera","MinimumTesseraVersion":"2.0.0","Capabilities":[{"Id":"local.time","Version":"1.0.0","Description":"Time","ExecutorKind":"native","AccountRequired":false,"RequiredPermissions":[],"SideEffectClass":"ReadOnly","TimeoutMilliseconds":1000,"MaxResultBytes":4096}]}""";
            var bytes = Encoding.UTF8.GetBytes(manifest);
            await File.WriteAllBytesAsync(Path.Combine(package, "manifest.json"), bytes);
            var catalogPath = Path.Combine(directory, "catalog.json");
            await File.WriteAllTextAsync(catalogPath, JsonSerializer.Serialize(new Dictionary<string,string>
            {
                ["local@1.0.0"] = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            }));
            var store = new SqliteKernelStore(Path.Combine(directory, "product.db"));
            await store.InitializeAsync();
            var ownerA = PrincipalRef.Create("https://tessera.test", "tenant", "owner-a", null, DateTimeOffset.UtcNow);
            var ownerB = PrincipalRef.Create("https://tessera.test", "tenant", "owner-b", null, DateTimeOffset.UtcNow);
            await store.AddAsync(ownerA);
            await store.AddAsync(ownerB);
            var catalog = new R2PluginCatalog(directory, catalogPath);

            await catalog.EnsureInstalledAsync(store, ownerA.PrincipalId, CancellationToken.None);
            await catalog.EnsureInstalledAsync(store, ownerA.PrincipalId, CancellationToken.None);
            await catalog.EnsureInstalledAsync(store, ownerB.PrincipalId, CancellationToken.None);

            Assert.Single(await store.ListPluginInstallationsAsync(ownerA.PrincipalId));
            Assert.Single(await store.ListPluginInstallationsAsync(ownerB.PrincipalId));
            Assert.Equal("local", (await store.ListPluginInstallationsAsync(ownerA.PrincipalId))[0].PluginId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
