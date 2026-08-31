using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Tessera.Core.Kernel;
using Tessera.Core.Stores;
using Tessera.Persistence.Sqlite;
using Tessera.Plugin.Abstractions;
using Xunit;

namespace Tessera.Broker.Tests;

/// <summary>
/// Database-backed regression tests for the safe-method principal-registration contract
/// (RFC 9110 §9.2.1): an authenticated read must change no product state, while an
/// authorized write must still establish the principal/FK state it needs.
///
/// Every authentication boundary is covered: the seven HTTP resolvers by real requests
/// against a real SQLite product/continuity database, and the plugin-host resolver
/// directly (first-party plugin assemblies are outside the broker by ADR 0032, so no
/// plugin route is composed into this host).
/// </summary>
public sealed class PrincipalRegistrationBoundaryTests : IAsyncLifetime
{
    private const string DevHeader = "X-Tessera-Dev-Principal";
    private const string DevIssuer = "https://dev.tessera.local";
    private const string DevTenant = "dev";
    private const string Writer = "alice@example.com";
    private const string Reader = "bob@example.com";

    /// <summary>One authenticated GET per HTTP authentication boundary/resolver.</summary>
    private static readonly (string Resolver, string Path)[] ReadRoutes =
    [
        ("R2ProductEndpoints.Boundary", "/api/v1/capabilities"),
        ("R2ProductEndpoints.Boundary", "/api/v1/conversations"),
        ("RemoteHostEndpoints.BoundaryAsync", "/api/v1/hosts"),
        ("ContinuityEndpoints.ResolveBoundaryAsync", "/portal/continuity/follow-ups"),
        ("IntegrationCatalogEndpoints.OwnerAsync", "/api/v1/integrations/sources"),
        ("SetupEndpoints.OwnerAsync", "/api/v1/setup/status"),
        ("ModelGatewayEndpoints.OwnerAsync", "/api/v1/settings/model-gateways"),
        ("RealtimeVoiceEndpoints.BoundaryAsync", "/api/v1/realtime-voice/status"),
    ];

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _directory = null!;
    private string _productDatabase = null!;
    private string _continuityDatabase = null!;

    public async Task InitializeAsync()
    {
        var port = FreePort();
        _directory = Directory.CreateTempSubdirectory("tessera-principal-registration-test").FullName;
        var configPath = Path.Combine(_directory, "tessera.json");
        await File.WriteAllTextAsync(configPath, $$"""
            {
              "server": { "host": "127.0.0.1", "port": {{port}} },
              "identity": { "mode": "dev", "trustDomain": "tessera.local" },
              "policy": { "default": "deny" },
              "audit": { "enabled": false }
            }
            """);
        var grantsPath = Path.Combine(_directory, "grants.json");
        await File.WriteAllTextAsync(grantsPath, "{ \"grants\": [], \"bindings\": [], \"recipes\": [] }");
        _productDatabase = Path.Combine(_directory, "product.db");
        _continuityDatabase = Path.Combine(_directory, "continuity.db");
        _app = await BrokerHost.BuildAppAsync(new BrokerHostOptions
        {
            ConfigPath = configPath,
            PolicyPath = grantsPath,
            StoreOverride = new InMemoryCredentialStore(),
            ProductDatabasePath = _productDatabase,
            ContinuityDatabasePath = _continuityDatabase,
        });
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* Best-effort test cleanup. */ }
    }

    [Fact]
    public async Task Authenticated_reads_change_no_product_state_and_register_no_principal()
    {
        // Seed real product state through a write so the reads have something to read.
        var conversationId = await CreateConversationAsync(Writer, "read-baseline-key");
        var before = await SnapshotAsync();

        foreach (var (resolver, path) in ReadRoutes)
        {
            using var response = await SendAsync(Reader, HttpMethod.Get, path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // Events and conversation detail for a conversation that really exists.
        using (var events = await SendAsync(Writer, HttpMethod.Get, $"/api/v1/conversations/{conversationId}/events"))
            Assert.Equal(HttpStatusCode.OK, events.StatusCode);
        using (var detail = await SendAsync(Writer, HttpMethod.Get, $"/api/v1/conversations/{conversationId}"))
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using (var otherEvents = await SendAsync(Reader, HttpMethod.Get, $"/api/v1/conversations/{conversationId}/events"))
            Assert.Equal(HttpStatusCode.OK, otherEvents.StatusCode);

        // The plugin-host resolver (BrokerPluginRequestIdentity.ResolveOwnerAsync).
        Assert.Equal(PrincipalIdOf(Reader), await ResolvePluginOwnerAsync(HttpMethod.Get, Reader));

        Assert.Equal(before, await SnapshotAsync());
        Assert.Null(await ProductStore.GetAsync(PrincipalIdOf(Reader)));
    }

    [Fact]
    public async Task Authorized_write_registers_exactly_one_principal_and_stays_idempotent()
    {
        var before = await SnapshotAsync();
        Assert.Null(await ProductStore.GetAsync(PrincipalIdOf(Writer)));

        var conversationId = await CreateConversationAsync(Writer, "write-registration-key");

        var principal = await ProductStore.GetAsync(PrincipalIdOf(Writer));
        Assert.NotNull(principal);
        Assert.Equal(
            PrincipalRef.Create(DevIssuer, DevTenant, Writer, Writer, DateTimeOffset.UtcNow).Issuer,
            principal.Issuer);
        Assert.Equal(DevTenant, principal.Tenant);
        Assert.Equal(Writer, principal.Subject);
        Assert.Equal(1, await CountAsync(_productDatabase, "principals") - before[$"product:principals"]);

        // The conversation row exists and is owned by the registered principal (FK satisfied).
        var conversation = await ProductStore.GetConversationAsync(PrincipalIdOf(Writer), conversationId);
        Assert.NotNull(conversation);
        Assert.Equal(PrincipalIdOf(Writer), conversation.OwnerPrincipalId);

        // Replaying the same idempotency key must not add a second principal row.
        Assert.Equal(conversationId, await CreateConversationAsync(Writer, "write-registration-key"));
        Assert.Equal(1, await CountAsync(_productDatabase, "principals") - before[$"product:principals"]);

        // The plugin-host resolver registers on an unsafe method too.
        Assert.Equal(PrincipalIdOf(Reader), await ResolvePluginOwnerAsync(HttpMethod.Post, Reader));
        Assert.NotNull(await ProductStore.GetAsync(PrincipalIdOf(Reader)));
    }

    [Fact]
    public async Task Unauthenticated_reads_and_writes_fail_closed_without_persistence()
    {
        var before = await SnapshotAsync();

        foreach (var (resolver, path) in ReadRoutes)
        {
            using var response = await _client.GetAsync(new Uri(path, UriKind.Relative));
            Assert.True(
                response.StatusCode is HttpStatusCode.Unauthorized,
                $"{resolver} GET {path} returned {(int)response.StatusCode}, expected 401.");
        }

        using (var write = await _client.PostAsJsonAsync(
            new Uri("/api/v1/conversations", UriKind.Relative),
            new { title = "unauthenticated" }))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
        }

        var context = new DefaultHttpContext { RequestServices = _app.Services };
        context.Request.Method = HttpMethods.Post;
        Assert.Null(await _app.Services.GetRequiredService<IPluginRequestIdentity>()
            .ResolveOwnerAsync(context, CancellationToken.None));

        Assert.Equal(before, await SnapshotAsync());
    }

    [Theory]
    [InlineData("GET", true)]
    [InlineData("HEAD", true)]
    [InlineData("OPTIONS", true)]
    [InlineData("TRACE", true)]
    [InlineData("POST", false)]
    [InlineData("PUT", false)]
    [InlineData("PATCH", false)]
    [InlineData("DELETE", false)]
    [InlineData("CONNECT", false)]
    [InlineData(null, false)]
    public void Safe_methods_follow_rfc_9110(string? method, bool expected)
        => Assert.Equal(expected, PrincipalRegistration.IsSafeMethod(method));

    private SqliteKernelStore ProductStore => _app.Services.GetRequiredService<SqliteKernelStore>();

    private static string PrincipalIdOf(string subject)
        => PrincipalRef.Create(DevIssuer, DevTenant, subject, subject, DateTimeOffset.UtcNow).PrincipalId;

    private async Task<string> ResolvePluginOwnerAsync(HttpMethod method, string subject)
    {
        var context = new DefaultHttpContext { RequestServices = _app.Services };
        context.Request.Method = method.Method;
        context.Request.Headers[DevHeader] = subject;
        var owner = await _app.Services.GetRequiredService<IPluginRequestIdentity>()
            .ResolveOwnerAsync(context, CancellationToken.None);
        Assert.NotNull(owner);
        return owner;
    }

    private async Task<string> CreateConversationAsync(string subject, string idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/conversations", UriKind.Relative))
        {
            Content = JsonContent.Create(new { title = "Principal registration" }),
        };
        request.Headers.Add(DevHeader, subject);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private Task<HttpResponseMessage> SendAsync(string subject, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(DevHeader, subject);
        return _client.SendAsync(request);
    }

    /// <summary>Row count of every table in both product databases, keyed by database and table.</summary>
    private async Task<IReadOnlyDictionary<string, long>> SnapshotAsync()
    {
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var (label, path) in new[] { ("product", _productDatabase), ("continuity", _continuityDatabase) })
        {
            await using var connection = await OpenReadOnlyAsync(path);
            foreach (var table in await ListTablesAsync(connection))
                counts[$"{label}:{table}"] = await CountAsync(connection, table);
        }

        return counts;
    }

    private static async Task<long> CountAsync(string databasePath, string table)
    {
        await using var connection = await OpenReadOnlyAsync(databasePath);
        return await CountAsync(connection, table);
    }

    private static async Task<long> CountAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        // `table` comes from sqlite_master / this test file, never from request input.
        command.CommandText = $"SELECT COUNT(*) FROM \"{table}\";";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<string>> ListTablesAsync(SqliteConnection connection)
    {
        var tables = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));
        return tables;
    }

    private static async Task<SqliteConnection> OpenReadOnlyAsync(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        return connection;
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
