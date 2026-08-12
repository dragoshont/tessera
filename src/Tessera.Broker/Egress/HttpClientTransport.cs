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
public sealed class HttpClientTransport : IHttpTransport, IStreamingHttpTransport, IDisposable
{
    private const int MaximumResponseBytes = 1024 * 1024;
    private readonly HttpClient _client;

    /// <summary>Creates the transport over an address guard (defaults to loopback-blocked).</summary>
    public HttpClientTransport(AddressGuard? addressGuard = null)
    {
        _client = CreateGuardedHttpClient(addressGuard);
    }

    /// <summary>
    /// Builds the same hardened, SSRF/rebind-guarded <see cref="HttpClient"/> the provider egress
    /// uses (no proxy, no auto-redirect, no ambient cookies, connect-time <see cref="AddressGuard"/>
    /// IP pinning). Reused for the OAuth-MCP discovery client so that RFC 9728/8414 probing of an
    /// untrusted upstream gets the identical connect-time SSRF defense.
    /// </summary>
    public static HttpClient CreateGuardedHttpClient(AddressGuard? addressGuard = null)
    {
        var guard = addressGuard ?? AddressGuard.Default;
        return new HttpClient(new SocketsHttpHandler
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
            if (!guard.IsAllowed(host, address))
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

        // Honor the caller's Content-Type — the OAuth token exchange is
        // application/x-www-form-urlencoded, NOT JSON. Defaulting every body to JSON
        // (the old behavior) silently corrupts a form POST: the upstream can't parse
        // grant_type and rejects it. Non-Content-Type headers pass through as request headers.
        string? contentType = null;
        foreach (var (name, value) in headers)
        {
            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                contentType = value;
            }
            else
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, contentType ?? "application/json");
        }

        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new TransportResponseTooLargeException(MaximumResponseBytes) { Source = exception.Source };
        }
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in response.Headers)
        {
            responseHeaders[name] = string.Join(", ", values);
        }

        return new TransportResponse((int)response.StatusCode, responseHeaders, text);
    }

    /// <inheritdoc/>
    public async Task<StreamingTransportResponse> SendStreamingAsync(
        string method,
        string url,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onChunk,
        int maximumResponseBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onChunk);
        if (maximumResponseBytes is < 1 or > MaximumResponseBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumResponseBytes));
        using var request = CreateRequest(method, url, headers, body);
        using var response = await _client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var responseHeaders = ResponseHeaders(response);
        if (!response.IsSuccessStatusCode)
        {
            try
            {
                await response.Content.LoadIntoBufferAsync(maximumResponseBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                throw new TransportResponseTooLargeException(maximumResponseBytes) { Source = exception.Source };
            }
            return new((int)response.StatusCode, responseHeaders,
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[8192];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total = checked(total + read);
            if (total > maximumResponseBytes)
                throw new TransportResponseTooLargeException(maximumResponseBytes);
            await onChunk(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return new((int)response.StatusCode, responseHeaders, null);
    }

    private static HttpRequestMessage CreateRequest(
        string method,
        string url,
        IReadOnlyDictionary<string, string> headers,
        string? body)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        string? contentType = null;
        foreach (var (name, value) in headers)
        {
            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase)) contentType = value;
            else request.Headers.TryAddWithoutValidation(name, value);
        }
        if (body is not null)
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, contentType ?? "application/json");
        return request;
    }

    private static Dictionary<string, string> ResponseHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in response.Headers) headers[name] = string.Join(", ", values);
        return headers;
    }

    /// <inheritdoc/>
    public void Dispose() => _client.Dispose();
}
