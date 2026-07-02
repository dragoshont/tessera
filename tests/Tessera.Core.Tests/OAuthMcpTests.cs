using System.Net;
using System.Text;
using Tessera.Core.Configuration;
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

        var discovery = new OAuthMcpDiscovery(new HttpClient(handler));

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
        var discovery = new OAuthMcpDiscovery(new HttpClient(handler));
        var endpoints = await discovery.DiscoverAsync("https://mob.test/.well-known/oauth-protected-resource");
        Assert.Null(endpoints);
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
}
