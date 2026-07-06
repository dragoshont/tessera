using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Tessera.Broker.Egress;
using Tessera.Core.Egress;
using Tessera.Core.OAuthMcp;
using Tessera.Core.Stores;
using Tessera.Providers.OAuthMcp;
using Xunit;
using McpHttpTransport = ModelContextProtocol.Client.HttpClientTransport;

namespace Tessera.Broker.Tests;

/// <summary>
/// Cross-repo conformance (ADR 0027, spec C): Tessera's REAL discovery + connect + acquirer +
/// refresh, run against the REAL running <c>mobbin-clone</c> OAuth AS (a separate process) — no
/// fakes on the OAuth path. Proves the C0+W integration end to end: RFC 9728/8414 discovery, the
/// authorization-code + PKCE round trip through the clone's <c>/oauth/authorize</c> +
/// <c>/oauth/token</c>, the per-principal bundle write, and a rotating refresh.
///
/// <para>OPT-IN: skipped unless <c>TESSERA_CONFORMANCE=1</c> AND the clone repo (+ its venv) is
/// present, so the normal suite stays hermetic and fast. Run it with:
/// <c>TESSERA_CONFORMANCE=1 dotnet test --filter Conformance</c>.</para>
/// </summary>
public sealed class OAuthMcpConformanceTests
{
    private const string RedirectUri = "http://127.0.0.1:8788/oauth/mcp/callback";

    private static string CloneDir =>
        Environment.GetEnvironmentVariable("MOBBIN_CLONE_DIR") ?? "/Users/dragoshont/Repo/mobbin-clone-mcp";

    private static bool Enabled =>
        Environment.GetEnvironmentVariable("TESSERA_CONFORMANCE") == "1"
        && File.Exists(Path.Combine(CloneDir, ".venv", "bin", "python"))
        && File.Exists(Path.Combine(CloneDir, "mobbin_clone", "server.py"));

    [Fact]
    public async Task Tessera_discovers_acquires_and_refreshes_against_the_real_clone_AS()
    {
        if (!Enabled)
        {
            return; // opt-in only (TESSERA_CONFORMANCE=1 + the clone present)
        }

        await using var clone = await Clic.StartAsync(CloneDir, RedirectUri);
        var baseUrl = clone.BaseUrl;

        // Real, loopback-permitting SSRF guards: the clone runs on 127.0.0.1:high-port over http,
        // which the production guards (AddressGuard.Default + https-only) rightly block.
        var addressGuard = new AddressGuard(allowLoopback: true);
        var ssrf = new SsrfGuard(["127.0.0.1"], allowPlainHttp: true);
        var store = new InMemoryCredentialStore();

        var discovery = new OAuthMcpDiscovery(Tessera.Broker.Egress.HttpClientTransport.CreateGuardedHttpClient(addressGuard), ssrf);
        var acquirer = new OAuthMcpAcquirer(new Tessera.Broker.Egress.HttpClientTransport(addressGuard), store, ssrf);
        var connect = new OAuthMcpConnectService(new InMemoryPendingAuthorizationStore(), acquirer);

        // 1) Discover the clone as an OAuth-MCP + begin the connect (real RFC 9728 probe → 8414 metadata).
        var start = await connect.BeginForRecipeAsync(
            discovery, $"{baseUrl}/mcp", ["screens.read"],
            principal: "alice@example.com", target: "mobbin", secretName: "mobbin-alice",
            new Uri(RedirectUri), clientId: "tessera");
        Assert.NotNull(start); // the clone answered 401 + resource_metadata and exposed a token endpoint

        // 2) Drive the clone's authorize endpoint (auto-approves) → 302 to our callback with code+state.
        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var authResp = await browser.GetAsync(start!.AuthorizeUrl);
        Assert.Equal(HttpStatusCode.Found, authResp.StatusCode);
        var redirect = authResp.Headers.Location!;
        var query = ParseQuery(redirect.Query);
        Assert.Equal(start.State, query["state"]); // the clone echoes our anti-forgery state
        var code = query["code"];

        // 3) Complete: redeem the code at the clone's REAL /oauth/token (PKCE verifier back-channel).
        var result = await connect.CompleteAsync(start.State, code);
        Assert.True(result.Status == OAuthAcquireStatus.Acquired, $"acquire failed: {result.Detail}");

        // The per-user token was written to the store, secretless to the caller.
        var bundle = await store.GetBundleAsync("mobbin-alice");
        Assert.False(bundle.IsEmpty);
        Assert.False(string.IsNullOrEmpty(bundle.AccessToken));
        Assert.False(string.IsNullOrEmpty(bundle.RefreshToken));

        // 4) Refresh against the clone's REAL token endpoint (rotating single-use ⇒ a fresh access token).
        var refreshed = await acquirer.RefreshStoredAsync("mobbin-alice", bundle);
        Assert.Equal(OAuthAcquireStatus.Acquired, refreshed.Status);
        var rotated = await store.GetBundleAsync("mobbin-alice");
        Assert.NotEqual(bundle.AccessToken, rotated.AccessToken);
    }

    private const string EntitledToken = "c2-entitled-access-token";
    private const string FreeTierToken = "c2-free-tier-token";

    /// <summary>
    /// C2 — egress conformance (ADR 0027, spec C / §3 P4). A REAL MCP client, holding ONLY its own
    /// caller identity, reaches the REAL running mobbin-clone THROUGH Tessera's <c>/v1/egress</c>:
    /// Tessera injects the per-user upstream bearer, and <c>tools/list</c> + a tool call come back
    /// with the real inline images + metadata. Then the free→entitled swap (402→200) is a
    /// Tessera-SIDE change the client never sees. No RecordingForwarder — the real YARP forward hits
    /// the real clone, so this catches green-but-dead the hermetic tests can't (it surfaced the
    /// default-port + connect-guard gaps that the P2b RecordingForwarder never exercised).
    /// </summary>
    [Fact]
    public async Task Tessera_fronts_the_clone_mcp_end_to_end_and_the_client_never_holds_a_token()
    {
        if (!Enabled)
        {
            return; // opt-in only (TESSERA_CONFORMANCE=1 + the clone present)
        }

        await using var clone = await Clic.StartAsync(
            CloneDir, RedirectUri, expectedToken: EntitledToken, freeTierToken: FreeTierToken);
        var clonePort = new Uri(clone.BaseUrl).Port;
        var mcpUrl = $"http://127.0.0.1:{clonePort}/mcp";

        // Tessera fronts the clone: egress on, 127.0.0.1 allow-listed (plain http, loopback via the
        // test AddressGuard seam), an oauth-mcp proxy recipe whose resource IS the clone's /mcp, and
        // the per-user bearer bound SERVER-SIDE. The real YARP forwarder is used (no override).
        var store = new InMemoryCredentialStore();
        store.Put("mobbin-cred", new CredentialBundle(AccessToken: EntitledToken));
        await using var app = await BuildTesseraFrontAsync(mcpUrl, store);
        var baseUrl = app.Urls.First();

        // The client carries ONLY its own caller identity — never the upstream bearer. onBehalfOf is
        // the user's forwarded TOKEN (the FakeTokenValidator resolves it to alice@example.com).
        var callerHeaders = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer apple-mcp-token",
            ["X-Tessera-On-Behalf-Of"] = "alice-token",
            ["X-Tessera-Upstream"] = mcpUrl,
        };
        var transport = new McpHttpTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri($"{baseUrl}/v1/egress/mobbin"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = callerHeaders,
        }, NullLoggerFactory.Instance);

        await using var client = await McpClient.CreateAsync(transport);

        // 1) tools/list THROUGH Tessera = the real three-tool surface (byte-identical tool names).
        var tools = await client.ListToolsAsync();
        var names = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("search_screens", names);
        Assert.Contains("search_flows", names);
        Assert.Contains("search_sections", names);

        // 2) a tool call THROUGH Tessera returns inline IMAGES + metadata carrying mobbin_url.
        var result = await client.CallToolAsync("search_screens", new Dictionary<string, object?>
        {
            ["query"] = "login screen with biometric authentication",
            ["platform"] = "ios",
        });
        Assert.False(result.IsError);
        Assert.Contains(result.Content, c => c is ImageContentBlock);
        Assert.Contains(result.Content, c => c is TextContentBlock text
            && text.Text.Contains("mobbin_url", StringComparison.Ordinal));

        // 3) 402 → 200 entitlement, a Tessera-SIDE swap the client never sees. The SAME caller request
        // is paywalled while the FREE token is bound, and served once an ENTITLED token is — proving the
        // upstream credential lives server-side, not with the client (the client held no token).
        using var raw = new HttpClient { BaseAddress = new Uri(baseUrl) };

        store.Put("mobbin-cred", new CredentialBundle(AccessToken: FreeTierToken));
        var paywalled = await raw.SendAsync(BuildInitializeProbe(mcpUrl));
        Assert.Equal(HttpStatusCode.PaymentRequired, paywalled.StatusCode); // 402 THROUGH Tessera

        store.Put("mobbin-cred", new CredentialBundle(AccessToken: EntitledToken));
        var served = await raw.SendAsync(BuildInitializeProbe(mcpUrl));
        Assert.Equal(HttpStatusCode.OK, served.StatusCode); // entitlement lifted, client unchanged

        await app.StopAsync();
    }

    /// <summary>A single-shot MCP <c>initialize</c> POST to Tessera's egress, carrying only the
    /// caller identity + the upstream header — used to read the raw HTTP status (402 vs 200) of the
    /// entitlement swap, where the clone's auth gate answers before the MCP layer.</summary>
    private static HttpRequestMessage BuildInitializeProbe(string mcpUrl)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/egress/mobbin")
        {
            Content = new StringContent(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"c2-probe\",\"version\":\"1\"}}}",
                System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer apple-mcp-token");
        req.Headers.TryAddWithoutValidation("X-Tessera-On-Behalf-Of", "alice-token");
        req.Headers.TryAddWithoutValidation("X-Tessera-Upstream", mcpUrl);
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        return req;
    }

    /// <summary>Builds + starts a Tessera broker that fronts the clone's <paramref name="mcpUrl"/> as
    /// an oauth-mcp proxy recipe, with the real YARP forwarder and a loopback-permitting connect guard
    /// (test seam) so the forward can reach the clone on 127.0.0.1.</summary>
    private static async Task<WebApplication> BuildTesseraFrontAsync(string mcpUrl, InMemoryCredentialStore store)
    {
        var port = FreePort();
        var dir = Directory.CreateTempSubdirectory("tessera-c2").FullName;
        var configPath = Path.Combine(dir, "tessera.json");
        File.WriteAllText(configPath, $$"""
            {
              "server": { "host": "127.0.0.1", "port": {{port}} },
              "identity": { "mode": "oidc", "oidc": { "issuer": "https://issuer.example/v2.0", "audience": "tessera" } },
              "policy": { "default": "deny", "manageRequiresStepUp": true },
              "audit": { "enabled": false },
              "egress": { "enabled": true, "allowedHosts": ["127.0.0.1"], "allowPlainHttp": true }
            }
            """);
        var grantsPath = Path.Combine(dir, "grants.json");
        File.WriteAllText(grantsPath, $$"""
            {
              "grants": [
                { "caller": "apple-mcp", "onBehalfOf": "alice@example.com", "target": "mobbin", "actions": ["read:mcp"] }
              ],
              "bindings": [
                { "target": "mobbin", "onBehalfOf": "alice@example.com", "credential": "mobbin-cred" }
              ],
              "recipes": [
                { "target": "mobbin", "egress": "proxy", "injection": "bearer",
                  "oauthMcp": { "mcpUrl": "{{mcpUrl}}" },
                  "tools": [
                    { "name": "search_screens", "action": "read:mcp", "path": "/mcp" },
                    { "name": "search_flows", "action": "read:mcp", "path": "/mcp" },
                    { "name": "search_sections", "action": "read:mcp", "path": "/mcp" }
                  ] }
              ]
            }
            """);

        var validator = new FakeTokenValidator()
            .AddApp("apple-mcp-token", "apple-mcp")
            .AddUser("alice-token", "alice-oid", "alice@example.com");

        var app = await BrokerHost.BuildAppAsync(new BrokerHostOptions
        {
            ConfigPath = configPath,
            PolicyPath = grantsPath,
            StoreOverride = store,
            ValidatorOverride = validator,
            AddressGuardOverride = new AddressGuard(allowLoopback: true),
        });
        await app.StartAsync();
        return app;
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var freePort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return freePort;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = pair.IndexOf('=', StringComparison.Ordinal);
            if (i > 0)
            {
                map[Uri.UnescapeDataString(pair[..i])] = Uri.UnescapeDataString(pair[(i + 1)..]);
            }
        }

        return map;
    }

    /// <summary>Starts (and stops) the mobbin-clone as a subprocess with its OAuth AS enabled.</summary>
    private sealed class Clic : IAsyncDisposable
    {
        private readonly Process _process;
        public string BaseUrl { get; }

        private Clic(Process process, string baseUrl)
        {
            _process = process;
            BaseUrl = baseUrl;
        }

        public static async Task<Clic> StartAsync(
            string cloneDir, string redirectUri, string? expectedToken = null, string? freeTierToken = null)
        {
            var port = FreePort();
            var baseUrl = $"http://127.0.0.1:{port}";
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(cloneDir, ".venv", "bin", "python"),
                Arguments = "-m mobbin_clone.server",
                WorkingDirectory = cloneDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.Environment["HOST"] = "127.0.0.1";
            psi.Environment["PORT"] = port.ToString();
            psi.Environment["PUBLIC_BASE_URL"] = baseUrl;
            psi.Environment["OAUTH_ALLOWED_REDIRECT_URIS"] = redirectUri;
            if (expectedToken is null && freeTierToken is null)
            {
                // C1 (acquisition-only): the MCP entitlement gate is not under test.
                psi.Environment["MCP_ALLOW_ANY_TOKEN"] = "1";
            }
            else
            {
                // C2 (egress conformance): exercise the REAL entitlement gate — an ENTITLED token is
                // served, the FREE-tier token is paywalled (402), anything else is refused.
                if (expectedToken is not null) psi.Environment["MCP_EXPECTED_TOKEN"] = expectedToken;
                if (freeTierToken is not null) psi.Environment["MCP_FREE_TIER_TOKEN"] = freeTierToken;
            }
            psi.Environment["PYTHONPATH"] = cloneDir;

            var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start mobbin-clone");
            await WaitForHealthAsync(baseUrl, process);
            return new Clic(process, baseUrl);
        }

        private static async Task WaitForHealthAsync(string baseUrl, Process process)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"mobbin-clone exited early ({process.ExitCode}): {await process.StandardError.ReadToEndAsync()}");
                }

                try
                {
                    var r = await http.GetAsync($"{baseUrl}/health");
                    if (r.IsSuccessStatusCode)
                    {
                        return;
                    }
                }
                catch (HttpRequestException)
                {
                    // not up yet
                }

                await Task.Delay(250);
            }

            throw new TimeoutException($"mobbin-clone did not become healthy at {baseUrl} within 30s");
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync();
                }
            }
            catch (InvalidOperationException)
            {
                // already gone
            }
            finally
            {
                _process.Dispose();
            }
        }

        private static int FreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
