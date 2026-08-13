using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Tessera.Core.Product;
using Xunit;

namespace Tessera.Broker.Tests;

public sealed class RemoteHostRequestReaderTests
{
    [Fact]
    public async Task Reader_preserves_duplicate_header_failures_and_reads_valid_requests()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var body = Encoding.UTF8.GetBytes("{}");
        var request = BuildRequest(
            key,
            HostAcceptedMessageOperations.LeaseAck,
            "lease-123",
            "msg-reader",
            "5",
            "1723600600",
            body);

        var parsed = await RemoteHostRequestReader.ReadAsync(
            request.Request,
            HostAcceptedMessageOperations.LeaseAck,
            "lease-123",
            CancellationToken.None);
        Assert.True(parsed.Succeeded);
        Assert.Equal("msg-reader", parsed.Envelope!.MessageId);
        Assert.Equal(body, parsed.Body);

        request = BuildRequest(
            key,
            HostAcceptedMessageOperations.LeaseAck,
            "lease-123",
            "msg-duplicate",
            "6",
            "1723600601",
            body);
        request.Request.Headers.Append("X-Tessera-Host-Id", "host-main");
        Assert.Equal(RemoteHostSignedRequestErrors.HostAuthInvalid, (await RemoteHostRequestReader.ReadAsync(
            request.Request,
            HostAcceptedMessageOperations.LeaseAck,
            "lease-123",
            CancellationToken.None)).Error);
    }

    [Theory]
    [InlineData("X-Tessera-Host-Id", "host-main,other")]
    [InlineData("X-Tessera-Host-Operation", " lease-ack")]
    [InlineData("X-Tessera-Host-Message-Id", "messagé")]
    public async Task Reader_rejects_comma_joined_whitespace_padded_and_non_ascii_headers(string name, string value)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = BuildRequest(
            key,
            HostAcceptedMessageOperations.LeaseAck,
            "lease-123",
            "msg-header",
            "7",
            "1723600602",
            Encoding.UTF8.GetBytes("{}"));
        request.Request.Headers[name] = value;

        var result = await RemoteHostRequestReader.ReadAsync(
            request.Request,
            HostAcceptedMessageOperations.LeaseAck,
            "lease-123",
            CancellationToken.None);
        Assert.Equal(RemoteHostSignedRequestErrors.HostAuthInvalid, result.Error);
    }

    [Fact]
    public async Task Reader_rejects_query_route_substitution_body_mismatch_and_oversized_input()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var body = Encoding.UTF8.GetBytes("{}");
        var query = BuildRequest(
            key,
            HostAcceptedMessageOperations.LeaseAck,
            "lease-123",
            "msg-query",
            "8",
            "1723600603",
            body,
            queryString: "?attempt=1");
        Assert.Equal(RemoteHostSignedRequestErrors.HostAuthInvalid, (await RemoteHostRequestReader.ReadAsync(
            query.Request,
            HostAcceptedMessageOperations.LeaseAck,
            "lease-123",
            CancellationToken.None)).Error);

        var routeMismatch = BuildRequest(
            key,
            HostAcceptedMessageOperations.LeaseAck,
            "lease-123",
            "msg-route",
            "9",
            "1723600604",
            body);
        Assert.Equal(RemoteHostSignedRequestErrors.HostAuthInvalid, (await RemoteHostRequestReader.ReadAsync(
            routeMismatch.Request,
            HostAcceptedMessageOperations.LeaseAck,
            "lease-other",
            CancellationToken.None)).Error);

        var bodyMismatch = BuildRequest(
            key,
            HostAcceptedMessageOperations.LeaseAck,
            "lease-123",
            "msg-body",
            "10",
            "1723600605",
            body);
        bodyMismatch.Request.Headers["X-Tessera-Host-Body-SHA256"] = new string('a', 64);
        Assert.Equal(RemoteHostSignedRequestErrors.HostAuthInvalid, (await RemoteHostRequestReader.ReadAsync(
            bodyMismatch.Request,
            HostAcceptedMessageOperations.LeaseAck,
            "lease-123",
            CancellationToken.None)).Error);

        var oversized = BuildRequest(
            key,
            HostAcceptedMessageOperations.Poll,
            "-",
            "msg-big",
            "11",
            "1723600606",
            new byte[RemoteHostProtocol.MaximumBodyBytes + 1]);
        Assert.Equal(RemoteHostSignedRequestErrors.HostAuthInvalid, (await RemoteHostRequestReader.ReadAsync(
            oversized.Request,
            HostAcceptedMessageOperations.Poll,
            "-",
            CancellationToken.None)).Error);
    }

    private static DefaultHttpContext BuildRequest(
        ECDsa key,
        string operation,
        string targetId,
        string messageId,
        string sequence,
        string timestamp,
        byte[] body,
        string queryString = "")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = targetId == "-" ? "/host-channel/poll" : $"/host-channel/leases/{targetId}/ack";
        context.Request.QueryString = new QueryString(queryString);
        context.Request.Body = new MemoryStream(body);
        context.Request.ContentLength = body.Length;
        var bodyHash = RemoteHostProtocol.ComputeBodyHash(body);
        var signature = SignCanonicalInput(key, operation, targetId, messageId, long.Parse(sequence), long.Parse(timestamp), bodyHash);
        context.Request.Headers["X-Tessera-Host-Id"] = "host-main";
        context.Request.Headers["X-Tessera-Host-Protocol-Version"] = "1";
        context.Request.Headers["X-Tessera-Host-Key-Version"] = "1";
        context.Request.Headers["X-Tessera-Host-Operation"] = operation;
        context.Request.Headers["X-Tessera-Host-Target-Id"] = targetId;
        context.Request.Headers["X-Tessera-Host-Message-Id"] = messageId;
        context.Request.Headers["X-Tessera-Host-Sequence"] = sequence;
        context.Request.Headers["X-Tessera-Host-Timestamp"] = timestamp;
        context.Request.Headers["X-Tessera-Host-Body-SHA256"] = bodyHash;
        context.Request.Headers["X-Tessera-Host-Signature"] = signature;
        return context;
    }

    private static string SignCanonicalInput(
        ECDsa key,
        string operation,
        string targetId,
        string messageId,
        long sequence,
        long timestamp,
        string bodySha256)
    {
        var signature = key.SignData(
            RemoteHostProtocol.BuildCanonicalSigningInput(
                "POST",
                operation,
                targetId,
                "host-main",
                1,
                1,
                messageId,
                sequence,
                timestamp,
                bodySha256),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Convert.ToBase64String(signature).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}