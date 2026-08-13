using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Tessera.Core.Product;

namespace Tessera.Broker;

internal sealed record RemoteHostRequestReadResult(
    HostSignedRequestEnvelope? Envelope,
    byte[] Body,
    string? Error)
{
    public bool Succeeded => Error is null;
}

internal static class RemoteHostRequestReader
{
    private static readonly string[] RequiredHeaders =
    [
        "X-Tessera-Host-Id",
        "X-Tessera-Host-Protocol-Version",
        "X-Tessera-Host-Key-Version",
        "X-Tessera-Host-Operation",
        "X-Tessera-Host-Target-Id",
        "X-Tessera-Host-Message-Id",
        "X-Tessera-Host-Sequence",
        "X-Tessera-Host-Timestamp",
        "X-Tessera-Host-Body-SHA256",
        "X-Tessera-Host-Signature",
    ];

    public static async Task<RemoteHostRequestReadResult> ReadAsync(
        HttpRequest request,
        string expectedOperation,
        string expectedTargetId,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            if (request.QueryString.HasValue)
                return Reject();

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in RequiredHeaders)
            {
                if (!request.Headers.TryGetValue(header, out var raw)
                    || raw.Count != 1
                    || raw[0] is not { } value
                    || value.Length == 0
                    || value.Contains(',', StringComparison.Ordinal)
                    || value != value.Trim()
                    || value.Any(character => character > sbyte.MaxValue))
                {
                    return Reject();
                }

                values.Add(header, value);
            }

            var body = await ReadBodyAsync(request, token).ConfigureAwait(false);
            var envelope = RemoteHostProtocol.ParseSignedRequest(
                request.Method,
                values["X-Tessera-Host-Operation"],
                values["X-Tessera-Host-Target-Id"],
                values["X-Tessera-Host-Id"],
                values["X-Tessera-Host-Protocol-Version"],
                values["X-Tessera-Host-Key-Version"],
                values["X-Tessera-Host-Message-Id"],
                values["X-Tessera-Host-Sequence"],
                values["X-Tessera-Host-Timestamp"],
                values["X-Tessera-Host-Body-SHA256"],
                values["X-Tessera-Host-Signature"]);
            if (!string.Equals(envelope.Operation, expectedOperation, StringComparison.Ordinal)
                || !string.Equals(envelope.TargetId, expectedTargetId, StringComparison.Ordinal)
                || !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(envelope.BodySha256),
                    Convert.FromHexString(RemoteHostProtocol.ComputeBodyHash(body))))
            {
                return Reject(body);
            }

            return new(envelope, body, null);
        }
        catch (ArgumentException)
        {
            return Reject();
        }
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken token)
    {
        if (request.ContentLength is > RemoteHostProtocol.MaximumBodyBytes)
            throw new ArgumentOutOfRangeException(nameof(request));
        if (!request.Body.CanSeek)
            request.EnableBuffering();
        request.Body.Position = 0;
        using var buffer = new MemoryStream(request.ContentLength is > 0 and <= RemoteHostProtocol.MaximumBodyBytes
            ? (int)request.ContentLength.Value
            : 0);
        var chunk = new byte[4096];
        while (true)
        {
            var read = await request.Body.ReadAsync(chunk.AsMemory(0, chunk.Length), token).ConfigureAwait(false);
            if (read == 0)
                break;
            if (buffer.Length + read > RemoteHostProtocol.MaximumBodyBytes)
                throw new ArgumentOutOfRangeException(nameof(request));
            await buffer.WriteAsync(chunk.AsMemory(0, read), token).ConfigureAwait(false);
        }

        if (request.Body.CanSeek)
            request.Body.Position = 0;
        return buffer.ToArray();
    }

    private static RemoteHostRequestReadResult Reject(byte[]? body = null)
        => new(null, body ?? [], RemoteHostSignedRequestErrors.HostAuthInvalid);
}