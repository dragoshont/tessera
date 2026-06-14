using Tessera.Core.Configuration;
using Tessera.Core.Identity;
using Tessera.Core.Policy;
using Tessera.Core.Resolution;
using Tessera.Core.Stores;
using Tessera.Providers;
using Xunit;

namespace Tessera.Broker.Tests;

/// <summary>
/// The MCP provider-tool listing (`tessera_list_provider_tools`) must carry each
/// tool's action plane (ADR 0019) so the chat can tell read/use/manage apart and
/// know which calls step up. Exercised through the real <see cref="BrokerProviderGateway"/>
/// over the shipped media example with egress enabled.
/// </summary>
public sealed class ProviderToolPlaneTests
{
    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new FileNotFoundException(relative);
    }

    [Fact]
    public void Listed_provider_tools_carry_their_action_plane()
    {
        var policy = ConfigLoader.LoadPolicy(FindRepoFile(Path.Combine("deploy", "config", "grants.media.example.json")));
        var pdp = new PolicyDecisionPoint(policy.Grants);
        var resolver = new CredentialResolver(policy.Bindings, new InMemoryCredentialStore());
        var config = new TesseraConfig
        {
            Egress = new EgressOptions
            {
                Enabled = true,
                AllowedHosts = ["seerr.example", "sonarr.example", "radarr.example", "qbittorrent.example"],
            },
        };

        var gateway = BrokerProviderGateway.Build(config, pdp, resolver, policy.Recipes, new NullTransport());

        // alice (operator) sees seerr tools spanning all three planes.
        var alice = new EndUserAssertion("alice@example.com", "https://issuer.example/v2.0", VerificationMethod.OidcJwt, "alice@example.com");
        var caller = new CallerIdentity("chat://librechat", VerificationMethod.OidcJwt, "tessera.local");
        var tools = gateway.ListTools(caller, alice);

        var search = tools.Single(t => t.Tool == "seerr_search");
        Assert.Equal("read", search.Plane);
        var request = tools.Single(t => t.Tool == "seerr_request");
        Assert.Equal("use", request.Plane);
        var settings = tools.Single(t => t.Tool == "seerr_settings");
        Assert.Equal("manage", settings.Plane);
        Assert.True(settings.Write); // manage settings is step-up/confirm-gated

        // bob (member) never sees the manage tool at all (not granted).
        var bob = new EndUserAssertion("bob@example.com", "https://issuer.example/v2.0", VerificationMethod.OidcJwt, "bob@example.com");
        var bobTools = gateway.ListTools(caller, bob);
        Assert.DoesNotContain(bobTools, t => t.Tool == "seerr_settings");
        Assert.Contains(bobTools, t => t.Tool == "seerr_request" && t.Plane == "use");
    }

    /// <summary>A transport that is never actually called (ListTools is a dry policy check).</summary>
    private sealed class NullTransport : IHttpTransport
    {
        public Task<TransportResponse> SendAsync(string method, string url, IReadOnlyDictionary<string, string> headers, string? body, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("ListTools must not perform an upstream call");
    }
}
