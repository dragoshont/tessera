using System.Net;
using System.Text;
using Tessera.Core.Configuration;
using Tessera.Core.Egress;
using Tessera.Core.OAuthMcp;
using Tessera.Core.Recipes;
using Xunit;

namespace Tessera.Core.Tests;

/// <summary>P1 (ADR 0027): RFC 9728/8414 discovery + the OAuth-MCP classifier + the
/// <c>oauth-mcp</c> recipe shape.</summary>
public class OAuthMcpTests
{
    // --- WWW-Authenticate challenge parsing ---------------------------------
    [Theory]
    [InlineData("Bearer resource_metadata=\"https://x/.well-known/oauth-protected-resource\"",
                "https://x/.well-known/oauth-protected-resource")]
    [InlineData("Bearer realm=\"x\", resource_metadata=\"https://y/rm\", error=\"invalid_token\"",
                "https://y/rm")]
    public void Challenge_extracts_resource_metadata(string header, string expected)
    {
        var c = OAuthChallenge.Parse(header);
        Assert.NotNull(c);
        Assert.Equal(expected, c!.ResourceMetadata);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Basic realm=\"x\"")]
    public void Challenge_parse_returns_null_for_non_bearer(string? header)
    {
        Assert.Null(OAuthChallenge.Parse(header));
    }

    // --- classifier ---------------------------------------------------------
    [Fact]
    public void Classifier_flags_oauth_mcp_only_on_401_with_resource_metadata()
    {
        var p = OAuthMcpClassifier.Classify(401, "Bearer resource_metadata=\"https://x/rm\"");
        Assert.True(p.IsOAuthMcp);
        Assert.Equal("https://x/rm", p.ResourceMetadataUrl);

        Assert.False(OAuthMcpClassifier.Classify(200, null).IsOAuthMcp);                        // success
        Assert.False(OAuthMcpClassifier.Classify(401, null).IsOAuthMcp);                        // 401, no challenge
        Assert.False(OAuthMcpClassifier.Classify(403, "Bearer resource_metadata=\"x\"").IsOAuthMcp); // wrong code
        Assert.False(OAuthMcpClassifier.Classify(401, "Basic realm=\"x\"").IsOAuthMcp);         // non-bearer
    }

    // --- metadata deserialization ------------------------------------------
    [Fact]
    public void Metadata_deserializes_rfc9728_and_rfc8414()
    {
        var pr = ProtectedResourceMetadata.FromJson(
            "{\"resource\":\"https://r/mcp\",\"authorization_servers\":[\"https://as\"],\"scopes_supported\":[\"a\"]}");
        Assert.Equal("https://r/mcp", pr!.Resource);
        Assert.Equal("https://as", pr.AuthorizationServers![0]);

        var asMeta = AuthorizationServerMetadata.FromJson(
            "{\"issuer\":\"https://as\",\"token_endpoint\":\"https://as/token\",\"code_challenge_methods_supported\":[\"S256\"]}");
        Assert.Equal("https://as/token", asMeta!.TokenEndpoint);
        Assert.True(asMeta.SupportsPkceS256);
    }

    // --- HTTP discovery (stub handler) -------------------------------------
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(fn(request));
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Discovery_probes_and_resolves_authorization_server()
    {
        var handler = new StubHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Post && url.EndsWith("/mcp", StringComparison.Ordinal))
            {
                var r = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                r.Headers.TryAddWithoutValidation(
                    "WWW-Authenticate",
                    "Bearer resource_metadata=\"https://mob.test/.well-known/oauth-protected-resource\"");
                return r;
            }
            if (url.EndsWith("/.well-known/oauth-protected-resource", StringComparison.Ordinal))
            {
                return Json("{\"resource\":\"https://mob.test/mcp\"," +
                            "\"authorization_servers\":[\"https://as.test\"]," +
                            "\"scopes_supported\":[\"screens.read\"]}");
            }
            if (url.EndsWith("/.well-known/oauth-authorization-server", StringComparison.Ordinal))
            {
                return Json("{\"issuer\":\"https://as.test\"," +
                            "\"authorization_endpoint\":\"https://as.test/authorize\"," +
                            "\"token_endpoint\":\"https://as.test/token\"," +
                            "\"code_challenge_methods_supported\":[\"S256\"]}");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var discovery = new OAuthMcpDiscovery(new HttpClient(handler), new SsrfGuard(["mob.test", "as.test"]));

        var probe = await discovery.ProbeAsync("https://mob.test/mcp");
        Assert.True(probe.IsOAuthMcp);
        Assert.Equal("https://mob.test/.well-known/oauth-protected-resource", probe.ResourceMetadataUrl);

        var endpoints = await discovery.DiscoverAsync(probe.ResourceMetadataUrl!);
        Assert.NotNull(endpoints);
        Assert.Equal("https://mob.test/mcp", endpoints!.Resource);
        Assert.Equal("https://as.test/token", endpoints.AuthorizationServer.TokenEndpoint);
        Assert.True(endpoints.AuthorizationServer.SupportsPkceS256);
        Assert.Contains("screens.read", endpoints.Scopes);
    }

    [Fact]
    public async Task Discovery_returns_null_when_no_authorization_server()
    {
        var handler = new StubHandler(req =>
            Json("{\"resource\":\"https://mob.test/mcp\",\"authorization_servers\":[]}"));
        var discovery = new OAuthMcpDiscovery(new HttpClient(handler), new SsrfGuard(["mob.test", "as.test"]));
        var endpoints = await discovery.DiscoverAsync("https://mob.test/.well-known/oauth-protected-resource");
        Assert.Null(endpoints);
    }

    [Fact]
    public async Task Discovery_refuses_an_authorization_server_off_the_allow_list()
    {
        // A hostile upstream points its authorization server at an off-allow-list host: discovery
        // must refuse (finding D1) rather than fetch it or hand back its endpoints.
        var handler = new StubHandler(_ =>
            Json("{\"resource\":\"https://mob.test/mcp\",\"authorization_servers\":[\"https://evil.test\"]}"));
        var discovery = new OAuthMcpDiscovery(new HttpClient(handler), new SsrfGuard(["mob.test"]));   // evil.test NOT allowed
        Assert.Null(await discovery.DiscoverAsync("https://mob.test/.well-known/oauth-protected-resource"));
    }

    [Fact]
    public async Task Discovery_refuses_an_authorize_endpoint_off_the_allow_list()
    {
        // The AS host is allow-listed, but it advertises an authorize endpoint on a DIFFERENT,
        // off-list host — the browser-redirect target. Discovery must refuse it (finding D1).
        var handler = new StubHandler(req =>
            req.RequestUri!.AbsolutePath.EndsWith("oauth-protected-resource", StringComparison.Ordinal)
                ? Json("{\"resource\":\"https://mob.test/mcp\",\"authorization_servers\":[\"https://as.test\"]}")
                : Json("{\"issuer\":\"https://as.test\",\"authorization_endpoint\":\"https://evil.test/authorize\",\"token_endpoint\":\"https://as.test/token\",\"code_challenge_methods_supported\":[\"S256\"]}"));
        var discovery = new OAuthMcpDiscovery(new HttpClient(handler), new SsrfGuard(["mob.test", "as.test"]));   // evil.test NOT allowed
        Assert.Null(await discovery.DiscoverAsync("https://mob.test/.well-known/oauth-protected-resource"));
    }

    // --- the oauth-mcp recipe shape (parse + round-trip through the loader) --
    [Fact]
    public void Recipe_model_carries_oauth_mcp_target()
    {
        var r = new Recipe("mobbin", OAuthMcp: new OAuthMcpTarget("https://mob.test/mcp", ["screens.read"]));
        Assert.NotNull(r.OAuthMcp);
        Assert.Equal("https://mob.test/mcp", r.OAuthMcp!.McpUrl);
        Assert.Equal(new[] { "screens.read" }, r.OAuthMcp.RequestedScopes);
    }

    [Fact]
    public void Policy_parses_and_round_trips_an_oauth_mcp_recipe()
    {
        var path = Path.GetTempFileName();
        var path2 = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,
                "{\"recipes\":[{\"target\":\"mobbin\",\"egress\":\"http\",\"injection\":\"bearer\"," +
                "\"upstreamBaseUrl\":\"https://mob.test\"," +
                "\"oauthMcp\":{\"mcpUrl\":\"https://mob.test/mcp\",\"scopes\":[\"screens.read\",\"flows.read\"]}}]}");

            var recipe = Assert.Single(ConfigLoader.LoadPolicy(path).Recipes);
            Assert.NotNull(recipe.OAuthMcp);
            Assert.Equal("https://mob.test/mcp", recipe.OAuthMcp!.McpUrl);
            Assert.Equal(new[] { "screens.read", "flows.read" }, recipe.OAuthMcp.RequestedScopes);

            // survives a SavePolicy -> LoadPolicy round-trip (the portal write path)
            ConfigLoader.SavePolicy(path2, ConfigLoader.LoadPolicy(path));
            var reloaded = Assert.Single(ConfigLoader.LoadPolicy(path2).Recipes);
            Assert.Equal("https://mob.test/mcp", reloaded.OAuthMcp!.McpUrl);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path2);
        }
    }

    // --- audience guard (ADR 0027 §4) ---------------------------------------
    [Theory]
    [InlineData("https://mob.test/mcp", true)]              // exact
    [InlineData("https://mob.test/mcp/", true)]             // trailing slash
    [InlineData("https://mob.test/mcp/messages", true)]     // subpath (SSE/session)
    [InlineData("https://mob.test/other", false)]           // sibling path
    [InlineData("https://mob.test/mcp2", false)]            // path-prefix trick
    [InlineData("https://evil.test/mcp", false)]            // different host (confused deputy)
    [InlineData("https://mob.test:8443/mcp", false)]        // different port
    [InlineData("http://mob.test/mcp", false)]              // downgraded scheme
    public void Audience_binds_token_to_its_resource(string upstream, bool bound)
    {
        Assert.Equal(bound, OAuthMcpAudience.IsBound(new Uri(upstream), "https://mob.test/mcp"));
    }

    [Fact]
    public void Audience_fails_closed_on_malformed_resource()
    {
        Assert.False(OAuthMcpAudience.IsBound(new Uri("https://mob.test/mcp"), "not-a-url"));
    }

    // --- MCP action classifier (P2b) ----------------------------------------
    [Theory]
    [InlineData("{\"method\":\"tools/list\"}", "tools/list", null)]
    [InlineData("{\"method\":\"initialize\",\"params\":{}}", "initialize", null)]
    [InlineData("{\"method\":\"tools/call\",\"params\":{\"name\":\"search_screens\",\"arguments\":{}}}", "tools/call", "search_screens")]
    public void Mcp_parse_extracts_method_and_tool(string body, string method, string? tool)
    {
        var call = McpActionClassifier.Parse(body);
        Assert.NotNull(call);
        Assert.Equal(method, call!.Method);
        Assert.Equal(tool, call.ToolName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[{\"method\":\"tools/list\"}]")]   // batch array => null
    [InlineData("{\"id\":1}")]                        // no method
    public void Mcp_parse_returns_null_for_non_jsonrpc(string body)
    {
        Assert.Null(McpActionClassifier.Parse(body));
    }

    [Fact]
    public void Mcp_classify_reads_protocol_and_declared_reads_writes_the_rest()
    {
        var declared = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["search_screens"] = false,   // declared read
            ["delete_board"] = true,      // declared write
        };
        Assert.Equal(McpAccess.Read, McpActionClassifier.Classify(new McpCall("tools/list", null), declared));
        Assert.Equal(McpAccess.Read, McpActionClassifier.Classify(new McpCall("initialize", null), declared));
        Assert.Equal(McpAccess.Read, McpActionClassifier.Classify(new McpCall("tools/call", "search_screens"), declared));
        Assert.Equal(McpAccess.Write, McpActionClassifier.Classify(new McpCall("tools/call", "delete_board"), declared));
        Assert.Equal(McpAccess.Write, McpActionClassifier.Classify(new McpCall("tools/call", "unknown_tool"), declared));  // undeclared => fail-safe
        Assert.Equal(McpAccess.Write, McpActionClassifier.Classify(new McpCall("tools/call", null), declared));            // malformed => fail-safe
    }

    [Fact]
    public void Mcp_classify_is_case_insensitive_for_tools_call()
    {
        // A lenient upstream that executes "Tools/Call" must not slip a mutating call past as a
        // read: any casing of tools/call is a tool call (finding AC2).
        var declared = new Dictionary<string, bool>(StringComparer.Ordinal) { ["delete_board"] = true };
        Assert.Equal(McpAccess.Write, McpActionClassifier.Classify(new McpCall("Tools/Call", "delete_board"), declared));
        Assert.Equal(McpAccess.Write, McpActionClassifier.Classify(new McpCall("TOOLS/CALL", "unknown"), declared));
        var call = McpActionClassifier.Parse("{\"method\":\"Tools/Call\",\"params\":{\"name\":\"delete_board\"}}");
        Assert.Equal("delete_board", call!.ToolName);
    }
}
