using System.Diagnostics;
using System.Net;
using Tessera.Broker.Egress;
using Tessera.Core.Egress;
using Tessera.Core.OAuthMcp;
using Tessera.Core.Stores;
using Tessera.Providers.OAuthMcp;
using Xunit;

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

        var discovery = new OAuthMcpDiscovery(HttpClientTransport.CreateGuardedHttpClient(addressGuard), ssrf);
        var acquirer = new OAuthMcpAcquirer(new HttpClientTransport(addressGuard), store, ssrf);
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

        public static async Task<Clic> StartAsync(string cloneDir, string redirectUri)
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
            psi.Environment["MCP_ALLOW_ANY_TOKEN"] = "1";
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
