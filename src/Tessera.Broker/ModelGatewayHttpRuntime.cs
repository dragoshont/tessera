using Tessera.Core.Configuration;
using Tessera.Providers;

namespace Tessera.Broker;

internal sealed class ModelGatewayHttpRuntime : IHttpTransport, IStreamingHttpTransport, IDisposable
{
    private readonly string[] _internalPrefixes;
    private readonly IHttpTransport _fallback;
    private readonly Egress.HttpClientTransport? _ownedTransport;
    private readonly IDisposable? _ownedFallback;

    public ModelGatewayHttpRuntime(
        ModelGatewayOptions options,
        IHttpTransport fallback,
        IHttpTransport? internalTransport = null,
        bool ownFallback = false)
    {
        _internalPrefixes = options.Endpoints
            .Select(item => new Uri(item.Endpoint.TrimEnd('/') + "/").AbsoluteUri)
            .ToArray();
        _fallback = fallback;
        _ownedFallback = ownFallback ? fallback as IDisposable : null;
        if (internalTransport is null)
        {
            var created = new Egress.HttpClientTransport(Tessera.Core.Egress.AddressGuard.Default);
            Transport = created;
            _ownedTransport = created;
        }
        else Transport = internalTransport;
    }

    public IHttpTransport Transport { get; }

    public Task<TransportResponse> SendAsync(
        string method,
        string url,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        CancellationToken cancellationToken = default)
    {
        var absolute = new Uri(url).AbsoluteUri;
        var selected = _internalPrefixes.Any(prefix => absolute.StartsWith(prefix, StringComparison.Ordinal))
            ? Transport
            : _fallback;
        return selected.SendAsync(method, url, headers, body, cancellationToken);
    }

    public Task<StreamingTransportResponse> SendStreamingAsync(
        string method,
        string url,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onChunk,
        int maximumResponseBytes,
        CancellationToken cancellationToken = default)
    {
        var absolute = new Uri(url).AbsoluteUri;
        var selected = _internalPrefixes.Any(prefix => absolute.StartsWith(prefix, StringComparison.Ordinal))
            ? Transport
            : _fallback;
        return selected is IStreamingHttpTransport streaming
            ? streaming.SendStreamingAsync(method, url, headers, body, onChunk, maximumResponseBytes, cancellationToken)
            : throw new NotSupportedException("The selected transport does not support streaming.");
    }

    public void Dispose()
    {
        _ownedTransport?.Dispose();
        _ownedFallback?.Dispose();
    }
}