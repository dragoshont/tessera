using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Tessera.Core.Product;
using Xunit;

namespace Tessera.Core.Tests;

public sealed class RemoteHostProtocolTests
{
    private static readonly BigInteger P256Order = new(
        Convert.FromHexString("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551"),
        isUnsigned: true,
        isBigEndian: true);
    private static readonly BigInteger P256HalfOrder = P256Order / 2;

    [Theory]
    [MemberData(nameof(CanonicalVectors))]
    public void Canonical_signing_input_and_request_hash_match_fixed_vectors(
        string operation,
        string targetId,
        string messageId,
        long sequence,
        long timestamp,
        string bodySha256,
        string expectedCanonical,
        string expectedRequestHash)
    {
        var envelope = RemoteHostProtocol.ParseSignedRequest(
            "POST",
            operation,
            targetId,
            "host-main",
            "1",
            "1",
            messageId,
            sequence.ToString(CultureInfo.InvariantCulture),
            timestamp.ToString(CultureInfo.InvariantCulture),
            bodySha256,
            Base64Url(new byte[64]));

        Assert.Equal(expectedCanonical, Encoding.UTF8.GetString(RemoteHostProtocol.BuildCanonicalSigningInput(envelope)));
        Assert.Equal(expectedRequestHash, envelope.RequestHash);
    }

    [Fact]
    public void Es256_verification_accepts_valid_low_s_signatures_and_rejects_malformed_variants()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = PublicKey(key);
        var envelope = RemoteHostProtocol.ParseSignedRequest(
            "POST",
            HostAcceptedMessageOperations.LeaseAck,
            "lease-123",
            "host-main",
            "1",
            "1",
            "msg-verify",
            "7",
            "1723600456",
            "44136fa355b3678a1146ad16f7e8649e94fb4fc21fef6f3fc490a0fdd9f9b403",
            SignCanonicalInput(key,
                "POST",
                HostAcceptedMessageOperations.LeaseAck,
                "lease-123",
                "host-main",
                1,
                1,
                "msg-verify",
                7,
                1723600456,
                "44136fa355b3678a1146ad16f7e8649e94fb4fc21fef6f3fc490a0fdd9f9b403",
                highS: false));

        Assert.True(RemoteHostProtocol.VerifyEs256Signature(envelope, publicKey));

        var derSignature = Base64Url(key.SignData(
            RemoteHostProtocol.BuildCanonicalSigningInput(envelope),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence));
        Assert.False(RemoteHostProtocol.VerifyEs256Signature(envelope with { Signature = envelope.Signature + "=" }, publicKey));
        Assert.False(RemoteHostProtocol.VerifyEs256Signature(envelope with { Signature = derSignature }, publicKey));
        Assert.False(RemoteHostProtocol.VerifyEs256Signature(envelope with
        {
            Signature = SignCanonicalInput(key,
                envelope.Method,
                envelope.Operation,
                envelope.TargetId,
                envelope.HostId,
                envelope.ProtocolVersion,
                envelope.KeyVersion,
                envelope.MessageId,
                envelope.Sequence,
                envelope.UnixTimestampSeconds,
                envelope.BodySha256,
                highS: true),
        }, publicKey));
        Assert.False(RemoteHostProtocol.VerifyEs256Signature(envelope with { Signature = Base64Url(BuildSignature(BigInteger.Zero, BigInteger.One)) }, publicKey));
        Assert.False(RemoteHostProtocol.VerifyEs256Signature(envelope with { Signature = Base64Url(BuildSignature(BigInteger.One, BigInteger.Zero)) }, publicKey));
        Assert.False(RemoteHostProtocol.VerifyEs256Signature(envelope with { Signature = Base64Url(BuildSignature(P256Order, BigInteger.One)) }, publicKey));
        Assert.False(RemoteHostProtocol.VerifyEs256Signature(envelope with { Sequence = envelope.Sequence + 1 }, publicKey));
    }

    [Theory]
    [InlineData("GET", HostAcceptedMessageOperations.Poll, "-", "host-main", "1", "1", "msg-1", "1", "1723600000", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("POST", "shell", "-", "host-main", "1", "1", "msg-1", "1", "1723600000", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("POST", HostAcceptedMessageOperations.Poll, "lease-1", "host-main", "1", "1", "msg-1", "1", "1723600000", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("POST", HostAcceptedMessageOperations.LeaseAck, "-", "host-main", "1", "1", "msg-1", "1", "1723600000", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("POST", HostAcceptedMessageOperations.Poll, "-", "Host-main", "1", "1", "msg-1", "1", "1723600000", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("POST", HostAcceptedMessageOperations.Poll, "-", "host-main", "01", "1", "msg-1", "1", "1723600000", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("POST", HostAcceptedMessageOperations.Poll, "-", "host-main", "1", "1", "msg-1", "0", "1723600000", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("POST", HostAcceptedMessageOperations.Poll, "-", "host-main", "1", "1", "msg-1", "1", "0253402300799", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [InlineData("POST", HostAcceptedMessageOperations.Poll, "-", "host-main", "1", "1", "msg-1", "1", "1723600000", "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855")]
    public void Canonical_parser_rejects_noncanonical_values(
        string method,
        string operation,
        string targetId,
        string hostId,
        string protocolVersion,
        string keyVersion,
        string messageId,
        string sequence,
        string timestamp,
        string bodySha256)
    {
        Assert.Throws<ArgumentException>(() => RemoteHostProtocol.ParseSignedRequest(
            method,
            operation,
            targetId,
            hostId,
            protocolVersion,
            keyVersion,
            messageId,
            sequence,
            timestamp,
            bodySha256,
            Base64Url(new byte[64])));
    }

    public static TheoryData<string, string, string, long, long, string, string, string> CanonicalVectors()
    {
        var data = new TheoryData<string, string, string, long, long, string, string, string>();
        data.Add(
            HostAcceptedMessageOperations.Poll,
            "-",
            "msg-1",
            1,
            1723600000,
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "TESSERA-HOST-V1\nPOST\npoll\n-\nhost-main\n1\n1\nmsg-1\n1\n1723600000\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "c835a50cb077766cbf71fe3f25638d100a6ed02083a583770747c37edc1144a1");
        data.Add(
            HostAcceptedMessageOperations.LeaseAck,
            "lease-123",
            "msg-2",
            42,
            1723600123,
            "44136fa355b3678a1146ad16f7e8649e94fb4fc21fef6f3fc490a0fdd9f9b403",
            "TESSERA-HOST-V1\nPOST\nlease-ack\nlease-123\nhost-main\n1\n1\nmsg-2\n42\n1723600123\n44136fa355b3678a1146ad16f7e8649e94fb4fc21fef6f3fc490a0fdd9f9b403",
            "9c428c0eacdf045ced0b7f3393e47c107d6cb52f19ecb3c84b3001df626a0dac");
        return data;
    }

    private static P256PublicJwk PublicKey(ECDsa key)
    {
        var parameters = key.ExportParameters(false);
        return RemoteHostValidation.NormalizeP256PublicJwk(
            $$"""{"kty":"EC","crv":"P-256","x":"{{Base64Url(parameters.Q.X!)}}","y":"{{Base64Url(parameters.Q.Y!)}}"}""");
    }

    private static string SignCanonicalInput(
        ECDsa key,
        string method,
        string operation,
        string targetId,
        string hostId,
        long protocolVersion,
        long keyVersion,
        string messageId,
        long sequence,
        long timestamp,
        string bodySha256,
        bool highS)
    {
        var bytes = key.SignData(
            RemoteHostProtocol.BuildCanonicalSigningInput(
                method,
                operation,
                targetId,
                hostId,
                protocolVersion,
                keyVersion,
                messageId,
                sequence,
                timestamp,
                bodySha256),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var r = new BigInteger(bytes.AsSpan(0, 32), isUnsigned: true, isBigEndian: true);
        var s = new BigInteger(bytes.AsSpan(32, 32), isUnsigned: true, isBigEndian: true);
        if (highS)
        {
            if (s <= P256HalfOrder)
                s = P256Order - s;
        }
        else if (s > P256HalfOrder)
        {
            s = P256Order - s;
        }
        return Base64Url(BuildSignature(r, s));
    }

    private static byte[] BuildSignature(BigInteger r, BigInteger s)
    {
        var bytes = new byte[64];
        CopyInteger(r, bytes.AsSpan(0, 32));
        CopyInteger(s, bytes.AsSpan(32, 32));
        return bytes;
    }

    private static void CopyInteger(BigInteger value, Span<byte> destination)
    {
        var source = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (source.Length > destination.Length)
            throw new ArgumentOutOfRangeException(nameof(value));
        source.CopyTo(destination[(destination.Length - source.Length)..]);
    }

    private static string Base64Url(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}