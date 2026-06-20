using Microsoft.AspNetCore.Http;
using Yarp.ReverseProxy.Forwarder;

namespace Tessera.Broker.Tests;

/// <summary>
/// A test <see cref="IHttpForwarder"/> that runs the egress transformer against a
/// fresh request and records the result — proving what would be sent upstream
/// (injected credential, stripped identity headers, pinned destination) without
/// opening a socket. Returns <see cref="ForwarderError.None"/> (a clean forward).
/// </summary>
internal sealed class RecordingForwarder : IHttpForwarder
{
    /// <summary>The transformed outbound request from the last forward, or null.</summary>
    public HttpRequestMessage? Captured { get; private set; }

    /// <summary>True once a forward was attempted (the request passed every gate).</summary>
    public bool Forwarded => Captured is not null;

    public ValueTask<ForwarderError> SendAsync(
        HttpContext context, string destinationPrefix, HttpMessageInvoker httpClient,
        ForwarderRequestConfig requestConfig, HttpTransformer transformer) =>
        SendAsync(context, destinationPrefix, httpClient, requestConfig, transformer, CancellationToken.None);

    public async ValueTask<ForwarderError> SendAsync(
        HttpContext context, string destinationPrefix, HttpMessageInvoker httpClient,
        ForwarderRequestConfig requestConfig, HttpTransformer transformer, CancellationToken cancellationToken)
    {
        var proxyRequest = new HttpRequestMessage(new HttpMethod(context.Request.Method), destinationPrefix);
        await transformer.TransformRequestAsync(context, proxyRequest, destinationPrefix, cancellationToken)
            .ConfigureAwait(false);
        Captured = proxyRequest;
        return ForwarderError.None;
    }
}
