using Tessera.Providers;

namespace Tessera.Broker.Egress;

/// <summary>
/// The real HTTP transport for provider egress (ADR 0014). A single, hardened
/// <see cref="HttpClient"/>: no proxy, no auto-redirect (an upstream can't bounce
/// us off the allow-listed host), no ambient cookies (every cookie is injected
/// explicitly), short timeout. The SSRF allow-list is enforced before we get here.
/// </summary>
public sealed class HttpClientTransport : IHttpTransport, IDisposable
{
    private readonly HttpClient _client;

    /// <summary>Creates the transport.</summary>
    public HttpClientTransport()
    {
        _client = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
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
