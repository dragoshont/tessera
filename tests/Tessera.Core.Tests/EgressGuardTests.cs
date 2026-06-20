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

    // ── AddressGuard.PublicOnly: also blocks the private/internal ranges ───────
    // The raw reverse-proxy egress reaches only public SaaS (iCloud, Graph), so a
    // rebind of an allow-listed name onto an internal address must be refused too.

    [Theory]
    [InlineData("10.0.0.5")]        // RFC 1918 10/8
    [InlineData("172.16.3.4")]      // RFC 1918 172.16/12 (low edge)
    [InlineData("172.31.255.254")]  // RFC 1918 172.16/12 (high edge)
    [InlineData("192.168.1.50")]    // RFC 1918 192.168/16
    [InlineData("100.64.0.1")]      // RFC 6598 CGNAT 100.64/10 (low edge)
    [InlineData("100.127.255.254")] // RFC 6598 CGNAT 100.64/10 (high edge)
    [InlineData("169.254.169.254")] // metadata — blocked regardless
    [InlineData("127.0.0.1")]       // loopback — blocked regardless
    [InlineData("fc00::1")]         // IPv6 unique-local fc00::/7
    [InlineData("fd12:3456::1")]    // IPv6 unique-local fd00::/8
    public void AddressGuard_PublicOnly_blocks_private_and_internal(string ip)
    {
        Assert.False(AddressGuard.PublicOnly.IsAllowed(IPAddress.Parse(ip)));
    }

    [Theory]
    [InlineData("17.253.144.10")]        // a public Apple address block
    [InlineData("8.8.8.8")]              // public
    [InlineData("172.15.0.1")]           // just OUTSIDE 172.16/12 → public
    [InlineData("172.32.0.1")]           // just OUTSIDE 172.16/12 → public
    [InlineData("100.63.255.255")]       // just OUTSIDE 100.64/10 → public
    [InlineData("100.128.0.1")]          // just OUTSIDE 100.64/10 → public
    [InlineData("2606:4700:4700::1111")] // public IPv6
    public void AddressGuard_PublicOnly_allows_public_unicast(string ip)
    {
        Assert.True(AddressGuard.PublicOnly.IsAllowed(IPAddress.Parse(ip)));
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

    // ── SsrfGuard: anchored host patterns (re:) for discovered partition hosts ──
    // iCloud's RFC 6764 discovery redirects caldav.icloud.com → pNN-caldav.icloud.com;
    // a tight anchored pattern allow-lists the partition family without a *.icloud.com
    // wildcard (which would cover far more than the DAV surface).

    private static readonly string[] IcloudAllowList =
    [
        "caldav.icloud.com",
        "contacts.icloud.com",
        "re:^p\\d{1,3}-(caldav|contacts)\\.icloud\\.com$",
    ];

    [Theory]
    [InlineData("https://caldav.icloud.com/")]                 // exact
    [InlineData("https://contacts.icloud.com/")]               // exact
    [InlineData("https://p1-caldav.icloud.com/123/")]          // pattern, 1 digit
    [InlineData("https://p52-caldav.icloud.com/123/cal/")]     // pattern, 2 digits
    [InlineData("https://p999-contacts.icloud.com/cards/")]    // pattern, 3 digits
    public void SsrfGuard_allows_icloud_exact_and_partition_hosts(string url)
    {
        Assert.True(new SsrfGuard(IcloudAllowList).IsAllowed(url));
    }

    [Theory]
    [InlineData("https://p1000-caldav.icloud.com/")]           // 4 digits — outside \d{1,3}
    [InlineData("https://p52-mail.icloud.com/")]               // wrong service
    [InlineData("https://caldav.icloud.com.evil.net/")]        // suffix attack — must NOT partial-match
    [InlineData("https://p52-caldav.icloud.com.attacker.net/")] // pattern as a prefix of a longer host
    [InlineData("https://evil-p52-caldav.icloud.com/")]        // prefix attack — anchored, must reject
    [InlineData("https://icloud.com/")]                        // bare apex not listed
    public void SsrfGuard_rejects_lookalike_and_unanchored_evasions(string url)
    {
        Assert.False(new SsrfGuard(IcloudAllowList).IsAllowed(url));
    }

    [Fact]
    public void SsrfGuard_pattern_full_match_is_enforced_even_without_anchors()
    {
        // A pattern the operator forgot to anchor must still only match the WHOLE host
        // (the guard span-checks the match), so it can't be used as a partial match.
        var guard = new SsrfGuard(["re:p\\d+-caldav\\.icloud\\.com"]); // no ^ $
        Assert.True(guard.IsAllowed("https://p7-caldav.icloud.com/"));
        Assert.False(guard.IsAllowed("https://p7-caldav.icloud.com.evil.net/"));
        Assert.False(guard.IsAllowed("https://evil.net/p7-caldav.icloud.com"));
    }

    [Fact]
    public void SsrfGuard_pattern_still_requires_https_by_default()
    {
        var guard = new SsrfGuard(IcloudAllowList);
        Assert.False(guard.IsAllowed("http://p52-caldav.icloud.com/")); // scheme gate still applies
    }
}
