namespace Tessera.Providers;

/// <summary>A raw HTTP response from the injectable transport (status + headers + body).</summary>
/// <param name="Status">HTTP status code.</param>
/// <param name="Headers">Response headers (case-insensitive).</param>
/// <param name="Body">Response body text.</param>
public sealed record TransportResponse(int Status, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>Headers plus an optional bounded error body from a streamed request.</summary>
public sealed record StreamingTransportResponse(
    int Status,
    IReadOnlyDictionary<string, string> Headers,
    string? ErrorBody);

public sealed class TransportResponseTooLargeException(long maximumBytes)
    : IOException($"Provider response exceeded the {maximumBytes} byte limit.");

/// <summary>
/// The injectable HTTP transport — a single method so the egress can be exercised
/// fully offline with a fake. The real implementation lives in the broker host.
/// </summary>
public interface IHttpTransport
{
    /// <summary>Performs one HTTP request and returns the raw response.</summary>
    Task<TransportResponse> SendAsync(
        string method,
        string url,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional transport capability for providers that expose incremental responses.
/// Implementations retain ownership of network policy, response bounds, and cancellation.
/// </summary>
public interface IStreamingHttpTransport
{
    Task<StreamingTransportResponse> SendStreamingAsync(
        string method,
        string url,
        IReadOnlyDictionary<string, string> headers,
        string? body,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> onChunk,
        int maximumResponseBytes,
        CancellationToken cancellationToken = default);
}
