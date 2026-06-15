using System.Net;
using System.Net.Sockets;
using Tessera.Core.Egress;
using Tessera.Providers;

namespace Tessera.Broker.Egress;

/// <summary>
/// The real HTTP transport for provider egress (ADR 0014). A single, hardened
/// <see cref="HttpClient"/>: no proxy, no auto-redirect (an upstream can't bounce
/// us off the allow-listed host), no ambient cookies (every cookie is injected
/// explicitly), short timeout. The host SSRF allow-list is enforced before we get
/// here; this transport adds the <em>connect-time</em> defense — it resolves the
/// host once, validates the resolved IP with an <see cref="AddressGuard"/>
/// (link-local/metadata/loopback blocked), and connects to that <b>pinned</b> IP,
/// so a DNS rebind can't swap in an internal address between check and connect
/// (the TOCTOU the OWASP/MCP SSRF guidance calls out).
/// </summary>
public sealed class HttpClientTransport : IHttpTransport, IDisposable
{
    private readonly HttpClient _client;

    /// <summary>Creates the transport over an address guard (defaults to loopback-blocked).</summary>
    public HttpClientTransport(AddressGuard? addressGuard = null)
    {
        var guard = addressGuard ?? AddressGuard.Default;
        _client = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            ConnectCallback = (context, cancellationToken) => ConnectAsync(guard, context, cancellationToken),
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    /// <summary>
    /// Resolves the target host, rejects any address the <see cref="AddressGuard"/>
    /// blocks, and connects to the first allowed <em>resolved</em> address — pinning
    /// it so the connection lands on the IP that was validated (no re-resolution,
    /// no rebind window). TLS, when the scheme is https, is layered by
    /// <see cref="HttpClient"/> over the returned stream against the original host.
    /// </summary>
    private static async ValueTask<Stream> ConnectAsync(
        AddressGuard guard, SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;

        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

        Exception? lastError = null;
        var anyAllowed = false;
        foreach (var address in addresses)
        {
            if (!guard.IsAllowed(address))
            {
                continue;
            }

            anyAllowed = true;
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                lastError = ex;
                socket.Dispose();
            }
        }

        if (!anyAllowed)
        {
            throw new IOException(
                $"egress blocked: host '{host}' resolved only to disallowed addresses (SSRF address guard — link-local/metadata/loopback)");
        }

        throw lastError ?? new IOException($"egress: could not connect to '{host}'");
    }

    /// <inheritdoc/>
    public async Task<TransportResponse> SendAsync(
        string method,
        string url,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (body is not null)
        {
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        }

        foreach (var (name, value) in headers)
        {
            // Content-Type is set on the content above; everything else is a request header.
            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in response.Headers)
        {
            responseHeaders[name] = string.Join(", ", values);
        }

        return new TransportResponse((int)response.StatusCode, responseHeaders, text);
    }

    /// <inheritdoc/>
    public void Dispose() => _client.Dispose();
}
