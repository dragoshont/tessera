using System.Net;
using System.Net.Sockets;

namespace Tessera.Core.Egress;

/// <summary>
/// Classifies a <em>resolved</em> IP address as safe or blocked for egress — the
/// SSRF defense that the host allow-list alone cannot give (OWASP SSRF Prevention;
/// MCP Security Best Practices). It is applied at <b>connect time</b> against the
/// address the host actually resolved to, so a DNS rebind cannot slip a
/// metadata/loopback IP past a name-based check (the TOCTOU the specs call out).
/// </summary>
/// <remarks>
/// It blocks the destinations that have <em>no legitimate reason</em> to receive an
/// injected credential — link-local (incl. the cloud-metadata IP
/// <c>169.254.169.254</c>), loopback (by default), multicast, broadcast, and the
/// unspecified address. It deliberately does <b>not</b> block the private ranges
/// (<c>10/8</c>, <c>172.16/12</c>, <c>192.168/16</c>, <c>fc00::/7</c>): internal
/// services — a homelab ClusterIP, a corporate API — live there legitimately and
/// are gated by the host allow-list (<see cref="SsrfGuard"/>) instead. IPv4-mapped
/// IPv6 (e.g. <c>::ffff:169.254.169.254</c>) is normalised first so an encoding
/// trick cannot dodge the range checks.
/// </remarks>
public sealed class AddressGuard
{
    private readonly bool _allowLoopback;

    /// <summary>Creates the guard. <paramref name="allowLoopback"/> permits loopback (dev/sidecar only).</summary>
    public AddressGuard(bool allowLoopback = false) => _allowLoopback = allowLoopback;

    /// <summary>The default guard (loopback blocked).</summary>
    public static readonly AddressGuard Default = new();

    /// <summary>True when <paramref name="address"/> is a safe egress destination.</summary>
    public bool IsAllowed(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // Normalise an IPv4-mapped IPv6 address to its IPv4 form so the range checks
        // can't be dodged with e.g. ::ffff:169.254.169.254.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return _allowLoopback; // 127.0.0.0/8, ::1
        }

        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return false; // 0.0.0.0 / ::
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 0)
            {
                return false; // 0.0.0.0/8 "this network"
            }

            if (b[0] == 169 && b[1] == 254)
            {
                return false; // 169.254.0.0/16 link-local — incl. 169.254.169.254 metadata
            }

            if (b[0] >= 224)
            {
                return false; // 224.0.0.0/4 multicast + 240.0.0.0/4 reserved + 255.255.255.255
            }

            return true; // public + private (10/8, 172.16/12, 192.168/16) — allow-list gates the host
        }

        // IPv6.
        if (address.IsIPv6LinkLocal)
        {
            return false; // fe80::/10
        }

        if (address.IsIPv6Multicast)
        {
            return false; // ff00::/8
        }

        return true; // global unicast + unique-local fc00::/7 (the v6 "private")
    }
}
