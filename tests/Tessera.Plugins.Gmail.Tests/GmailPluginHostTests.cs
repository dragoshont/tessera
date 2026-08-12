using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Tessera.Broker;
using Tessera.Core.Stores;
using Tessera.Persistence.Sqlite;
using Tessera.Plugin.Abstractions;
using Tessera.Providers;
using Xunit;

namespace Tessera.Plugins.Gmail.Tests;

public sealed class GmailPluginHostTests
{
    [Fact]
    public async Task Discovered_plugin_owns_OAuth_callback_and_account_connection()
    {
        var directory = Directory.CreateTempSubdirectory("tessera-gmail-plugin-host").FullName;
        WebApplication? app = null;
        HttpClient? client = null;
        try
        {
            var port = FreePort();
            var moduleRoot = Path.Combine(directory, "modules");
            Directory.CreateDirectory(moduleRoot);
            var bytes = await File.ReadAllBytesAsync(typeof(GmailPlugin).Assembly.Location);
            const string fileName = "Tessera.Plugins.Gmail.dll";
            await File.WriteAllBytesAsync(Path.Combine(moduleRoot, fileName), bytes);
            var moduleCatalog = Path.Combine(directory, "modules.json");
            await File.WriteAllTextAsync(moduleCatalog, JsonSerializer.Serialize(new[]
            {
                new PluginModuleArtifact(
                    "gmail",
                    "1.0.0",
                    fileName,
                    Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    PluginTrustState.BUILT_IN),
            }));
            var configPath = Path.Combine(directory, "tessera.json");
            await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
            {
                server = new { host = "127.0.0.1", port },
                identity = new { mode = "dev", trustDomain = "tessera.local" },
                policy = new { @default = "deny" },
                audit = new { enabled = false },
                gmailOAuth = new
                {
                    enabled = true,
                    clientId = "gmail-client",
                    clientSecretRef = "google-client-secret",
                    redirectUri = $"http://127.0.0.1:{port}/oauth/gmail/callback",
                    scopes = new[] { "https://www.googleapis.com/auth/gmail.readonly" },
                },
            }));
            var grants = Path.Combine(directory, "grants.json");
            await File.WriteAllTextAsync(grants, "{\"grants\":[],\"bindings\":[],\"recipes\":[]}");
            var custody = new InMemoryCredentialStore();
            await custody.PutBundleAsync(
                "google-client-secret",
                new CredentialBundle(Extra: new Dictionary<string, string>
                {
                    [GmailOAuthService.ClientSecretExtraKey] = "client-secret-value",
                }));
            var transport = new GmailOAuthTransport();
            app = await BrokerHost.BuildAppAsync(new BrokerHostOptions
            {
                ConfigPath = configPath,
                PolicyPath = grants,
                StoreOverride = custody,
                TransportOverride = transport,
                ProductDatabasePath = Path.Combine(directory, "product.db"),
                PluginRoot = RepositoryPluginRoot(),
                PluginModuleRoot = moduleRoot,
                PluginModuleCatalogPath = moduleCatalog,
            });
            await app.StartAsync();
            client = new() { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

            using var begin = new HttpRequestMessage(HttpMethod.Post, "/api/v1/accounts/gmail/connect")
            {
                Content = JsonContent.Create(new { displayName = "My Gmail" }),
            };
            begin.Headers.Add("X-Tessera-Dev-Principal", "owner@example.com");
            var beginResponse = await client.SendAsync(begin);
            Assert.Equal(HttpStatusCode.OK, beginResponse.StatusCode);
            var authorize = new Uri(JsonDocument.Parse(await beginResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("authorizeUrl").GetString()!);
            var state = System.Web.HttpUtility.ParseQueryString(authorize.Query)["state"];

            var callback = await client.GetAsync($"/oauth/gmail/callback?code=one-time-code&state={Uri.EscapeDataString(state!)}");
            Assert.Equal(HttpStatusCode.OK, callback.StatusCode);
            using var accountsRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/accounts");
            accountsRequest.Headers.Add("X-Tessera-Dev-Principal", "owner@example.com");
            var accounts = JsonDocument.Parse(await (await client.SendAsync(accountsRequest)).Content.ReadAsStringAsync()).RootElement.GetProperty("items");
            var account = Assert.Single(accounts.EnumerateArray());
            Assert.Equal("user@example.com", account.GetProperty("providerAccountId").GetString());
            Assert.Equal("CONNECTED", account.GetProperty("lifecycle").GetString());
            Assert.Contains("client_secret=client-secret-value", Assert.Single(transport.RequestBodies), StringComparison.Ordinal);
        }
        finally
        {
            client?.Dispose();
            if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    private static string RepositoryPluginRoot()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../plugins"));
        return Directory.Exists(path) ? path : throw new DirectoryNotFoundException(path);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class GmailOAuthTransport : IHttpTransport
    {
        public List<string> RequestBodies { get; } = [];

        public Task<TransportResponse> SendAsync(
            string method,
            string url,
            IReadOnlyDictionary<string, string> headers,
            string? body,
            CancellationToken cancellationToken = default)
        {
            if (url == "https://oauth2.googleapis.com/token")
            {
                RequestBodies.Add(body ?? "");
                return Task.FromResult(new TransportResponse(200, new Dictionary<string, string>(), "{\"access_token\":\"gmail-access\",\"refresh_token\":\"gmail-refresh\",\"scope\":\"https://www.googleapis.com/auth/gmail.readonly\"}"));
            }
            if (url.EndsWith("/profile", StringComparison.Ordinal))
                return Task.FromResult(new TransportResponse(200, new Dictionary<string, string>(), "{\"emailAddress\":\"user@example.com\",\"messagesTotal\":10,\"threadsTotal\":7,\"historyId\":\"12345\"}"));
            if (url.Contains("/messages?", StringComparison.Ordinal))
                return Task.FromResult(new TransportResponse(200, new Dictionary<string, string>(), "{\"messages\":[],\"resultSizeEstimate\":0}"));
            return Task.FromResult(new TransportResponse(404, new Dictionary<string, string>(), "{}"));
        }
    }
}