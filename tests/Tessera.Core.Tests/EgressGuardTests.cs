using System.Net;
using Tessera.Core.Egress;
using Xunit;

namespace Tessera.Core.Tests;

/// <summary>
/// The egress SSRF guards (OWASP SSRF Prevention; MCP Security Best Practices).
/// <see cref="SsrfGuard"/> gates the host + scheme; <see cref="AddressGuard"/> gates
/// the resolved IP at connect time (the DNS-rebind/TOCTOU defense the host check
/// alone can't give).
/// </summary>
public sealed class EgressGuardTests
{
    // ── AddressGuard: dangerous ranges blocked, internal ranges allowed ────────

    [Theory]
    [InlineData("169.254.169.254")] // cloud metadata (AWS/GCP/Azure) — the headline SSRF target
    [InlineData("169.254.0.1")]     // link-local 169.254.0.0/16
    [InlineData("127.0.0.1")]       // loopback (blocked by default)
    [InlineData("0.0.0.0")]         // unspecified
    [InlineData("224.0.0.1")]       // multicast
    [InlineData("255.255.255.255")] // broadcast
    [InlineData("::1")]             // IPv6 loopback
    [InlineData("fe80::1")]         // IPv6 link-local
    [InlineData("::ffff:169.254.169.254")] // IPv4-mapped IPv6 metadata — must normalise + block
    public void AddressGuard_blocks_dangerous_addresses(string ip)
    {
        Assert.False(AddressGuard.Default.IsAllowed(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("10.0.0.5")]        // private 10/8 — a homelab ClusterIP lives here
    [InlineData("172.16.3.4")]      // private 172.16/12
    [InlineData("192.168.1.50")]    // private 192.168/16
    [InlineData("8.8.8.8")]         // public
    [InlineData("fc00::1")]         // IPv6 unique-local (the v6 "private")
    [InlineData("2606:4700:4700::1111")] // public IPv6
    public void AddressGuard_allows_internal_and_public_unicast(string ip)
    {
        // The host allow-list gates *which* hosts; the address guard only blocks the
        // dangerous ranges, so private/internal addresses stay reachable.
        Assert.True(AddressGuard.Default.IsAllowed(IPAddress.Parse(ip)));
    }

    [Fact]
    public void AddressGuard_can_permit_loopback_when_opted_in()
    {
        var devGuard = new AddressGuard(allowLoopback: true);
        Assert.True(devGuard.IsAllowed(IPAddress.Loopback));
        Assert.True(devGuard.IsAllowed(IPAddress.IPv6Loopback));
        // …but still blocks metadata even with loopback allowed.
        Assert.False(devGuard.IsAllowed(IPAddress.Parse("169.254.169.254")));
    }

    // ── SsrfGuard: host allow-list + scheme opt-in ────────────────────────────

    [Fact]
    public void SsrfGuard_allows_https_to_an_allow_listed_host()
    {
        var guard = new SsrfGuard(["api.example.com"]);
        Assert.True(guard.IsAllowed("https://api.example.com/v1/items"));
    }

    [Fact]
    public void SsrfGuard_refuses_a_host_off_the_allow_list()
    {
        var guard = new SsrfGuard(["api.example.com"]);
        Assert.False(guard.IsAllowed("https://evil.example.com/"));
    }

    [Fact]
    public void SsrfGuard_refuses_plain_http_by_default()
    {
        var guard = new SsrfGuard(["sonarr.internal"]);
        Assert.False(guard.IsAllowed("http://sonarr.internal/api/v3/series"));
    }

    [Fact]
    public void SsrfGuard_permits_plain_http_to_allow_listed_hosts_when_opted_in()
    {
        // The internal-services opt-in: http allowed, but ONLY to allow-listed hosts.
        var guard = new SsrfGuard(["sonarr.internal"], allowPlainHttp: true);
        Assert.True(guard.IsAllowed("http://sonarr.internal/api/v3/series"));
        Assert.False(guard.IsAllowed("http://evil.internal/")); // host still gated
    }

    [Fact]
    public void SsrfGuard_refuses_non_http_schemes()
    {
        var guard = new SsrfGuard(["api.example.com"], allowPlainHttp: true);
        Assert.False(guard.IsAllowed("ftp://api.example.com/"));
        Assert.False(guard.IsAllowed("file:///etc/passwd"));
    }

    [Fact]
    public void SsrfGuard_empty_allow_list_permits_nothing()
    {
        var guard = new SsrfGuard([]);
        Assert.False(guard.IsAllowed("https://api.example.com/"));
    }
}
