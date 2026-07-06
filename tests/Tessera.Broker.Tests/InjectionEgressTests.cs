using Tessera.Broker.Egress;
using Tessera.Core.Egress;
using Xunit;

namespace Tessera.Broker.Tests;

/// <summary>
/// The per-recipe connect-guard posture (ADR 0027 §4 / OWASP SSRF). A CalDAV/public-SaaS proxy
/// recipe reaches only public providers (<see cref="AddressGuard.PublicOnly"/> — loopback AND
/// private refused); an oauth-mcp recipe may front an internal/in-cluster MCP so it uses
/// <see cref="AddressGuard.Default"/> (private/ClusterIP reachable; loopback/link-local/metadata
/// still refused), matching the provider path. Combined with EgressGuardTests (which proves
/// Default allows RFC 1918 and PublicOnly blocks it), these pin down that an oauth-mcp upstream can
/// reach a private ClusterIP while the CalDAV proxy cannot.
/// </summary>
public sealed class InjectionEgressTests
{
    [Fact]
    public void Proxy_recipes_use_the_public_only_connect_guard()
    {
        // The CalDAV/iCloud proxy posture is unchanged: loopback AND private/internal refused.
        Assert.Same(AddressGuard.PublicOnly, InjectionEgress.ConnectGuardFor(isOAuthMcp: false, oauthMcpOverride: null));
    }

    [Fact]
    public void OAuthMcp_recipes_use_the_default_guard_so_an_in_cluster_ClusterIP_is_reachable()
    {
        // AddressGuard.Default allows RFC 1918 (a homelab/in-cluster ClusterIP) while still blocking
        // loopback/link-local/metadata — the §4 in-cluster rollout target becomes reachable, matching
        // the provider path (HttpClientTransport).
        Assert.Same(AddressGuard.Default, InjectionEgress.ConnectGuardFor(isOAuthMcp: true, oauthMcpOverride: null));
    }

    [Fact]
    public void The_test_override_relaxes_only_the_oauth_mcp_path_never_the_proxy_path()
    {
        var permissive = new AddressGuard(allowLoopback: true);
        Assert.Same(permissive, InjectionEgress.ConnectGuardFor(isOAuthMcp: true, oauthMcpOverride: permissive));
        // The override must NOT weaken the CalDAV/public-SaaS proxy posture.
        Assert.Same(AddressGuard.PublicOnly, InjectionEgress.ConnectGuardFor(isOAuthMcp: false, oauthMcpOverride: permissive));
    }
}
