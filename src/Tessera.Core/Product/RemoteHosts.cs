using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tessera.Core.Product;

public static class HostPairingStates
{
    public const string Issued = "ISSUED";
    public const string Claimed = "CLAIMED";
    public const string Confirmed = "CONFIRMED";
    public const string Expired = "EXPIRED";
    public const string Canceled = "CANCELED";

    public static bool IsValid(string value) => value is Issued or Claimed or Confirmed or Expired or Canceled;
}

public static class RemoteHostLifecycles
{
    public const string Pairing = "PAIRING";
    public const string Online = "ONLINE";
    public const string Busy = "BUSY";
    public const string Degraded = "DEGRADED";
    public const string Offline = "OFFLINE";
    public const string Revoked = "REVOKED";
    public const string UpdateRequired = "UPDATE_REQUIRED";

    public static bool IsValid(string value) => value is Pairing or Online or Busy or Degraded or Offline or Revoked or UpdateRequired;
}

public static class HostAcceptedMessageOperations
{
    public const string Poll = "poll";
    public const string LeaseAck = "lease-ack";
    public const string LeaseEvents = "lease-events";
    public const string LeaseComplete = "lease-complete";
    public const string LeaseReconcile = "lease-reconcile";
    public const string LeaseArtifact = "lease-artifact";

    public static bool IsValid(string value)
        => value is Poll or LeaseAck or LeaseEvents or LeaseComplete or LeaseReconcile or LeaseArtifact;
}

public static class JobExecutionLocations
{
    public const string Server = "SERVER";
    public const string Host = "HOST";
    public const string AnyCompatibleHost = "ANY_COMPATIBLE_HOST";

    public static bool IsValid(string value)
        => value is Server or Host or AnyCompatibleHost;
}

public static class JobExecutionFallbackPolicies
{
    public const string None = "NONE";

    public static bool IsValid(string value)
        => value == None;
}

public static class JobRunBlockerCodes
{
    public const string WaitingForHost = "WAITING_FOR_HOST";
    public const string WaitingForCapability = "WAITING_FOR_CAPABILITY";
    public const string WaitingForResource = "WAITING_FOR_RESOURCE";
    public const string HostDisconnected = "HOST_DISCONNECTED";
    public const string HostUpdateRequired = "HOST_UPDATE_REQUIRED";

    public static bool IsValid(string value)
        => value is WaitingForHost or WaitingForCapability or WaitingForResource
            or HostDisconnected or HostUpdateRequired;
}

public static class HostLeaseStates
{
    public const string Offered = "OFFERED";
    public const string Acknowledged = "ACKNOWLEDGED";
    public const string Running = "RUNNING";
    public const string Completed = "COMPLETED";
    public const string Failed = "FAILED";
    public const string ReconciliationRequired = "RECONCILIATION_REQUIRED";
    public const string Declined = "DECLINED";
    public const string Expired = "EXPIRED";
    public const string Revoked = "REVOKED";
    public const string Disconnected = "DISCONNECTED";

    public static bool IsValid(string value)
        => value is Offered or Acknowledged or Running or Completed or Failed
            or ReconciliationRequired or Declined or Expired or Revoked or Disconnected;
}

public static class HostLeaseCompletionOutcomes
{
    public const string Succeeded = "SUCCEEDED";
    public const string Failed = "FAILED";
    public const string Unknown = "UNKNOWN";

    public static bool IsValid(string value)
        => value is Succeeded or Failed or Unknown;
}

public static class HostPollAttemptStates
{
    public const string NotStarted = "NOT_STARTED";
    public const string Started = "STARTED";
    public const string Completed = "COMPLETED";

    public static bool IsValid(string value)
        => value is NotStarted or Started or Completed;
}

public static class HostLeaseEventTypes
{
    public const string HostConnected = "HOST_CONNECTED";
    public const string HostDisconnected = "HOST_DISCONNECTED";
    public const string JobAccepted = "JOB_ACCEPTED";
    public const string StepStarted = "STEP_STARTED";
    public const string StepCompleted = "STEP_COMPLETED";
    public const string ApprovalRequired = "APPROVAL_REQUIRED";
    public const string JobFailed = "JOB_FAILED";
    public const string JobCompleted = "JOB_COMPLETED";

    public static bool IsValid(string value)
        => value is HostConnected or HostDisconnected or JobAccepted or StepStarted
            or StepCompleted or ApprovalRequired or JobFailed or JobCompleted;
}

public static class HostArtifactKinds
{
    public const string Text = "TEXT";

    public static bool IsValid(string value) => value == Text;
}

public static class HostArtifactMediaTypes
{
    public const string TextPlain = "text/plain";

    public static bool IsValid(string value) => value == TextPlain;
}

public static class HostArtifactRetentions
{
    public const string Run = "RUN";

    public static bool IsValid(string value) => value == Run;
}

public static class HostArtifactContentStates
{
    public const string Available = "AVAILABLE";

    public static bool IsValid(string value) => value == Available;
}

public sealed record HostSignedRequestEnvelope(
    string Method,
    string Operation,
    string TargetId,
    string HostId,
    long ProtocolVersion,
    long KeyVersion,
    string MessageId,
    long Sequence,
    long UnixTimestampSeconds,
    string BodySha256,
    string Signature,
    string RequestHash);

public static class RemoteHostSignedRequestErrors
{
    public const string HostAuthInvalid = "host_auth_invalid";
    public const string HostRevoked = "host_revoked";
    public const string HostReplay = "host_replay";
    public const string HostSequenceInvalid = "host_sequence_invalid";
    public const string HostClockSkew = "host_clock_skew";
    public const string HostProtocolUnsupported = "host_protocol_unsupported";
    public const string HostRequestTooLarge = "host_request_too_large";

    public static bool IsValid(string value)
        => value is HostAuthInvalid or HostRevoked or HostReplay or HostSequenceInvalid
            or HostClockSkew or HostProtocolUnsupported or HostRequestTooLarge;

    public static int StatusCode(string value) => value switch
    {
        HostRequestTooLarge => 413,
        HostAuthInvalid => 401,
        HostProtocolUnsupported => 409,
        HostRevoked or HostReplay or HostSequenceInvalid or HostClockSkew => 409,
        _ => 400,
    };
}

public static class RemoteHostProtocol
{
    public const string CanonicalPrefix = "TESSERA-HOST-V1";
    public const long SupportedProtocolVersion = 1;
    public const long SupportedKeyVersion = 1;
    public const long MaximumUnixTimestampSeconds = 253402300799;
    public const long MaximumClockSkewSeconds = 300;
    public const int MaximumBodyBytes = 64 * 1024;
    public const int MaximumArtifactBodyBytes = 256 * 1024;
    public const int MaximumArtifactRequestBodyBytes = MaximumArtifactBodyBytes * 6 + 16 * 1024;

    private const int CoordinateLength = 32;
    private const int SignatureLength = 64;
    private static readonly BigInteger P256Order = new(
        Convert.FromHexString("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551"),
        isUnsigned: true,
        isBigEndian: true);
    private static readonly BigInteger P256HalfOrder = P256Order / 2;

    public static HostSignedRequestEnvelope ParseSignedRequest(
        string method,
        string operation,
        string targetId,
        string hostId,
        string protocolVersion,
        string keyVersion,
        string messageId,
        string sequence,
        string unixTimestampSeconds,
        string bodySha256,
        string signature)
    {
        ValidateMethod(method, nameof(method));
        ValidateOperation(operation, nameof(operation));
        ValidateTarget(operation, targetId, nameof(targetId));
        RemoteHostValidation.ValidateIdentifier(hostId, nameof(hostId));
        RemoteHostValidation.ValidateIdentifier(messageId, nameof(messageId));
        var parsedProtocolVersion = ParseCanonicalDecimal(
            protocolVersion, 19, 0, long.MaxValue, nameof(protocolVersion));
        var parsedKeyVersion = ParseCanonicalDecimal(
            keyVersion, 19, 0, long.MaxValue, nameof(keyVersion));
        var parsedSequence = ParseCanonicalDecimal(
            sequence, 19, 1, long.MaxValue, nameof(sequence));
        var parsedTimestamp = ParseCanonicalDecimal(
            unixTimestampSeconds, 12, 0, MaximumUnixTimestampSeconds, nameof(unixTimestampSeconds));
        RemoteHostValidation.ValidateLowerHex(bodySha256, 64, nameof(bodySha256));
        _ = DecodeCanonicalBase64Url(signature, SignatureLength, nameof(signature));
        var requestHash = Convert.ToHexStringLower(SHA256.HashData(BuildCanonicalSigningInput(
            method,
            operation,
            targetId,
            hostId,
            parsedProtocolVersion,
            parsedKeyVersion,
            messageId,
            parsedSequence,
            parsedTimestamp,
            bodySha256)));
        return new(
            method,
            operation,
            targetId,
            hostId,
            parsedProtocolVersion,
            parsedKeyVersion,
            messageId,
            parsedSequence,
            parsedTimestamp,
            bodySha256,
            signature,
            requestHash);
    }

    public static byte[] BuildCanonicalSigningInput(HostSignedRequestEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return BuildCanonicalSigningInput(
            envelope.Method,
            envelope.Operation,
            envelope.TargetId,
            envelope.HostId,
            envelope.ProtocolVersion,
            envelope.KeyVersion,
            envelope.MessageId,
            envelope.Sequence,
            envelope.UnixTimestampSeconds,
            envelope.BodySha256);
    }

    public static byte[] BuildCanonicalSigningInput(
        string method,
        string operation,
        string targetId,
        string hostId,
        long protocolVersion,
        long keyVersion,
        string messageId,
        long sequence,
        long unixTimestampSeconds,
        string bodySha256)
    {
        ValidateMethod(method, nameof(method));
        ValidateOperation(operation, nameof(operation));
        ValidateTarget(operation, targetId, nameof(targetId));
        RemoteHostValidation.ValidateIdentifier(hostId, nameof(hostId));
        RemoteHostValidation.ValidateIdentifier(messageId, nameof(messageId));
        ValidateVersion(protocolVersion, nameof(protocolVersion));
        ValidateVersion(keyVersion, nameof(keyVersion));
        ValidateSequence(sequence, nameof(sequence));
        ValidateTimestamp(unixTimestampSeconds, nameof(unixTimestampSeconds));
        RemoteHostValidation.ValidateLowerHex(bodySha256, 64, nameof(bodySha256));
        var canonical = string.Join('\n',
            CanonicalPrefix,
            method,
            operation,
            targetId,
            hostId,
            protocolVersion.ToString(CultureInfo.InvariantCulture),
            keyVersion.ToString(CultureInfo.InvariantCulture),
            messageId,
            sequence.ToString(CultureInfo.InvariantCulture),
            unixTimestampSeconds.ToString(CultureInfo.InvariantCulture),
            bodySha256);
        return Encoding.UTF8.GetBytes(canonical);
    }

    public static string ComputeBodyHash(ReadOnlySpan<byte> body)
        => Convert.ToHexStringLower(SHA256.HashData(body));

    public static bool UsesSupportedProtocol(HostSignedRequestEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return envelope.ProtocolVersion == SupportedProtocolVersion
            && envelope.KeyVersion == SupportedKeyVersion;
    }

    public static bool HasAcceptableClockSkew(
        HostSignedRequestEnvelope envelope,
        DateTimeOffset now,
        long maximumClockSkewSeconds = MaximumClockSkewSeconds)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (maximumClockSkewSeconds < 0 || maximumClockSkewSeconds > MaximumClockSkewSeconds)
            throw new ArgumentOutOfRangeException(nameof(maximumClockSkewSeconds));
        return Math.Abs(now.ToUnixTimeSeconds() - envelope.UnixTimestampSeconds) <= maximumClockSkewSeconds;
    }

    public static bool VerifyEs256Signature(HostSignedRequestEnvelope envelope, P256PublicJwk publicKey)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(publicKey);
        byte[] signature;
        try
        {
            signature = DecodeCanonicalBase64Url(envelope.Signature, SignatureLength, nameof(envelope.Signature));
        }
        catch (ArgumentException)
        {
            return false;
        }

        var r = new BigInteger(signature.AsSpan(0, CoordinateLength), isUnsigned: true, isBigEndian: true);
        var s = new BigInteger(signature.AsSpan(CoordinateLength, CoordinateLength), isUnsigned: true, isBigEndian: true);
        if (r <= BigInteger.Zero || r >= P256Order || s <= BigInteger.Zero || s >= P256Order || s > P256HalfOrder)
            return false;

        try
        {
            using var key = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint
                {
                    X = DecodeCanonicalBase64Url(publicKey.X, CoordinateLength, nameof(publicKey)),
                    Y = DecodeCanonicalBase64Url(publicKey.Y, CoordinateLength, nameof(publicKey)),
                },
            });
            return key.VerifyData(
                BuildCanonicalSigningInput(envelope),
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public static void ValidateTarget(string operation, string targetId, string parameterName)
    {
        ValidateOperation(operation, nameof(operation));
        if (operation == HostAcceptedMessageOperations.Poll)
        {
            if (targetId != "-")
                throw new ArgumentException("Poll targets must be '-'.", parameterName);
            return;
        }

        RemoteHostValidation.ValidateIdentifier(targetId, parameterName);
    }

    private static void ValidateMethod(string method, string parameterName)
    {
        ValidateAscii(method, parameterName);
        if (method != "POST")
            throw new ArgumentException("Host requests must use POST.", parameterName);
    }

    private static void ValidateOperation(string operation, string parameterName)
    {
        ValidateAscii(operation, parameterName);
        if (!HostAcceptedMessageOperations.IsValid(operation))
            throw new ArgumentException("Host operation is not supported.", parameterName);
    }

    private static void ValidateVersion(long value, string parameterName)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(parameterName);
    }

    private static void ValidateSequence(long value, string parameterName)
    {
        if (value < 1)
            throw new ArgumentOutOfRangeException(parameterName);
    }

    private static void ValidateTimestamp(long value, string parameterName)
    {
        if (value < 0 || value > MaximumUnixTimestampSeconds)
            throw new ArgumentOutOfRangeException(parameterName);
    }

    private static long ParseCanonicalDecimal(
        string value,
        int maximumDigits,
        long minimum,
        long maximum,
        string parameterName)
    {
        ValidateAscii(value, parameterName);
        if (value.Length > maximumDigits
            || (value.Length > 1 && value[0] == '0')
            || value.Any(character => character is < '0' or > '9')
            || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < minimum
            || parsed > maximum)
        {
            throw new ArgumentException("Value is not a canonical decimal.", parameterName);
        }

        return parsed;
    }

    private static void ValidateAscii(string value, string parameterName)
    {
        if (string.IsNullOrEmpty(value) || value.Any(character => character is < '!' or > '~'))
            throw new ArgumentException("Value must be visible ASCII.", parameterName);
    }

    private static byte[] DecodeCanonicalBase64Url(string value, int expectedLength, string parameterName)
    {
        if (string.IsNullOrEmpty(value)
            || value.Contains('=', StringComparison.Ordinal)
            || value.Any(character => character is not (>= 'A' and <= 'Z')
                and not (>= 'a' and <= 'z')
                and not (>= '0' and <= '9')
                and not '-'
                and not '_'))
        {
            throw new ArgumentException("Value is not canonical base64url.", parameterName);
        }

        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(
                value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Value is not canonical base64url.", parameterName, exception);
        }

        if (decoded.Length != expectedLength || Base64UrlEncode(decoded) != value)
            throw new ArgumentException("Value is not canonical base64url.", parameterName);
        return decoded;
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record P256PublicJwk(string CanonicalJson, string X, string Y, string Thumbprint);

public sealed record JobExecutionPolicy(
    string OwnerPrincipalId,
    string JobId,
    string Location,
    string? PreferredHostId,
    IReadOnlyList<(string Id, string Version)> RequiredCapabilities,
    IReadOnlyList<string> RequiredResourceIds,
    string FallbackPolicy,
    long Version);

public sealed record JobRunBlocker(
    string OwnerPrincipalId,
    string RunId,
    string Code,
    string? HostId,
    string? CapabilityId,
    string? ResourceId,
    string? DetailCode,
    DateTimeOffset ObservedAt,
    DateTimeOffset? ClearedAt,
    long Version);

public sealed record HostLeaseResource(
    string OwnerPrincipalId,
    string LeaseId,
    string ResourceId,
    long ResourceGrantVersion,
    string AccessMode,
    string Fingerprint);

public sealed record HostLeaseEvent(
    string OwnerPrincipalId,
    string LeaseId,
    string EventId,
    long Sequence,
    string Type,
    DateTimeOffset OccurredAt,
    string? Summary,
    string? DataJson);

public sealed record HostWorkLease(
    string OwnerPrincipalId,
    string LeaseId,
    string RunId,
    string JobId,
    string HostId,
    long SchedulerFence,
    long Attempt,
    string ProfileId,
    string CapabilityId,
    string CapabilityVersion,
    long CapabilityGrantVersion,
    string InputHash,
    string State,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExecuteUntil,
    DateTimeOffset? AcknowledgedAt,
    DateTimeOffset? CompletedAt,
    string? LocalAttemptId,
    string? Outcome,
    string? OutputSha256,
    string? FailureCode,
    long Version);

public sealed record HostResourceGrantTuple(
    string ResourceId,
    long ResourceGrantVersion,
    string AccessMode,
    string Fingerprint);

public sealed record HostPollActiveAttempt(
    string LeaseId,
    string LocalAttemptId,
    string State);

public sealed record HostArtifact(
    string OwnerPrincipalId,
    string ArtifactId,
    string RunId,
    string LeaseId,
    string? ActionId,
    string Kind,
    string MediaType,
    string Summary,
    int SizeBytes,
    string Sha256,
    string Retention,
    string ContentState,
    bool Redacted,
    bool Truncated,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    long Version);

public sealed record HostArtifactDetail(
    HostArtifact Artifact,
    string TextContent,
    string? EvidenceId);

public sealed record NormalizedRemoteHostOutput(
    string Text,
    bool Redacted,
    bool Truncated,
    string Sha256,
    int SizeBytes);

public static partial class RemoteHostOutputNormalizer
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static NormalizedRemoteHostOutput Normalize(ReadOnlySpan<byte> input, int limitBytes = 32 * 1024)
    {
        if (limitBytes is < 1 or > RemoteHostProtocol.MaximumArtifactBodyBytes)
            throw new ArgumentOutOfRangeException(nameof(limitBytes));
        var decoded = StrictUtf8.GetString(input);
        var normalized = decoded.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        normalized = new string(normalized.Where(character => character is '\n' or '\t' || !char.IsControl(character)).ToArray());
        var redacted = false;
        normalized = RedactQuotedSensitiveFields(normalized, ref redacted);
        normalized = Redact(normalized, SecretPattern(), ref redacted, match =>
        {
            return $"{match.Groups[1].Value}[REDACTED]";
        });
        normalized = Redact(normalized, SensitiveFieldPattern(), ref redacted, _ => "[REDACTED]");
        normalized = Redact(normalized, EnvironmentAssignmentPattern(), ref redacted, match =>
            $"{match.Groups[1].Value}[REDACTED]");
        normalized = Redact(normalized, AbsolutePathPattern(), ref redacted, match =>
            $"{match.Groups[1].Value}[REDACTED]");
        normalized = Redact(normalized, PemBlockPattern(), ref redacted, _ => "[REDACTED]");
        var bytes = Encoding.UTF8.GetBytes(normalized);
        var truncated = bytes.Length > limitBytes;
        var length = Math.Min(bytes.Length, limitBytes);
        while (length > 0 && length < bytes.Length && (bytes[length] & 0xC0) == 0x80) length--;
        var persisted = bytes.AsSpan(0, length).ToArray();
        return new(
            StrictUtf8.GetString(persisted),
            redacted,
            truncated,
            Convert.ToHexStringLower(SHA256.HashData(persisted)),
            persisted.Length);
    }

    private static string Redact(string value, Regex pattern, ref bool redacted, MatchEvaluator replacement)
    {
        var matched = false;
        var result = pattern.Replace(value, match =>
        {
            matched = true;
            return replacement(match);
        });
        redacted |= matched;
        return result;
    }

    private static string RedactQuotedSensitiveFields(string value, ref bool redacted)
    {
        StringBuilder? result = null;
        var copyFrom = 0;
        for (var index = 0; index < value.Length; index++)
        {
            string key;
            int separator;
            var quote = value[index];
            if (quote is '"' or '\'')
            {
                var keyEnd = FindQuotedValueEnd(value, index + 1, quote, 128);
                if (keyEnd < 0)
                    continue;
                key = value[(index + 1)..keyEnd];
                separator = keyEnd + 1;
            }
            else
            {
                if (!IsFieldNameCharacter(value[index])
                    || index > 0 && IsFieldNameCharacter(value[index - 1]))
                {
                    continue;
                }
                var keyEnd = index;
                while (keyEnd < value.Length
                    && keyEnd - index <= 128
                    && IsFieldNameCharacter(value[keyEnd]))
                {
                    keyEnd++;
                }
                if (keyEnd == index || keyEnd - index > 128)
                    continue;
                key = value[index..keyEnd];
                separator = keyEnd;
            }

            while (separator < value.Length && char.IsWhiteSpace(value[separator])) separator++;
            if (separator >= value.Length || value[separator] != ':')
                continue;
            separator++;
            while (separator < value.Length && char.IsWhiteSpace(value[separator])) separator++;
            if (separator >= value.Length || value[separator] is not ('"' or '\''))
                continue;

            if (!IsSensitiveQuotedField(key))
                continue;
            var valueQuote = value[separator];
            var valueEnd = FindQuotedValueEnd(value, separator + 1, valueQuote, int.MaxValue);
            if (valueEnd < 0)
                valueEnd = value.Length;

            result ??= new StringBuilder(value.Length);
            result.Append(value, copyFrom, separator + 1 - copyFrom);
            result.Append("[REDACTED]");
            copyFrom = valueEnd;
            index = valueEnd;
            redacted = true;
        }

        if (result is null)
            return value;
        result.Append(value, copyFrom, value.Length - copyFrom);
        return result.ToString();
    }

    private static int FindQuotedValueEnd(string value, int start, char quote, int maximumCharacters)
    {
        var escaped = false;
        for (var index = start; index < value.Length; index++)
        {
            if (index - start > maximumCharacters)
                return -1;
            var character = value[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (character == '\\')
            {
                escaped = true;
                continue;
            }
            if (character == quote)
                return index;
        }
        return -1;
    }

    private static bool IsFieldNameCharacter(char value)
        => value is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '_' or '-';

    private static bool IsSensitiveQuotedField(string key)
    {
        var normalized = key.ToLowerInvariant();
        var compact = normalized.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        return compact.Contains("apikey", StringComparison.Ordinal)
            || compact.Contains("token", StringComparison.Ordinal)
            || compact.Contains("password", StringComparison.Ordinal)
            || compact.Contains("secret", StringComparison.Ordinal)
            || compact.Contains("authorization", StringComparison.Ordinal)
            || compact.Contains("signature", StringComparison.Ordinal)
            || compact.Contains("publickey", StringComparison.Ordinal)
            || compact.Contains("privatekey", StringComparison.Ordinal)
            || compact.EndsWith("path", StringComparison.Ordinal)
            || compact.Contains("command", StringComparison.Ordinal)
            || compact.Contains("argv", StringComparison.Ordinal)
            || compact.EndsWith("environment", StringComparison.Ordinal)
            || compact.EndsWith("env", StringComparison.Ordinal);
    }

    [GeneratedRegex("(?i)(authorization\\s*[:=]\\s*(?:bearer\\s+)?|(?:api[_-]?key|token|password|secret)\\s*[:=]\\s*)[^\\s,;]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SecretPattern();

    [GeneratedRegex("(?i)((?:signature|(?:public|private)\\s+key|path|command|argv|environment|env)\\s*[:=]\\s*)[^\\n]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SensitiveFieldPattern();

    [GeneratedRegex("(?m)(^|\\s)([A-Z][A-Z0-9_]{1,63}\\s*=\\s*)[^\\s,;]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex EnvironmentAssignmentPattern();

    [GeneratedRegex("(?i)(^|[^A-Za-z0-9_:/])(?:~/[^\\s,;\"']+|/(?:Users|home|Volumes|System|Network|Developer|private|tmp|var|opt|usr|bin|sbin|dev|etc|Applications|Library|workspace|root)/[^\\s,;\"']+|[A-Za-z]:\\\\[^\\s,;\"']+)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex AbsolutePathPattern();

    [GeneratedRegex("(?is)-----BEGIN [^-]+-----.*?(?:-----END [^-]+-----|\\z)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex PemBlockPattern();
}

public sealed record RemoteJobRunProjection(
    JobRunBlocker? Blocker,
    HostWorkLease? Lease,
    RemoteHost? Host,
    IReadOnlyList<JobRunCheckpoint> Checkpoints,
    IReadOnlyList<HostArtifact> Artifacts);

public sealed record HostCapabilityAdvertisement(
    string OwnerPrincipalId, string HostId, string CapabilityId, string CapabilityVersion,
    string SchemaHash, string SideEffectClass, DateTimeOffset AdvertisedAt);

public sealed record HostCapabilityGrant(
    string OwnerPrincipalId, string HostId, string CapabilityId, string CapabilityVersion,
    DateTimeOffset GrantedAt, DateTimeOffset? RevokedAt, long Version);

public sealed record HostResource(
    string OwnerPrincipalId, string HostId, string ResourceId, string Type, string DisplayName,
    string Fingerprint, string State, DateTimeOffset AdvertisedAt, long Version);

public sealed record HostResourceGrant(
    string OwnerPrincipalId, string HostId, string ResourceId, string AccessMode,
    DateTimeOffset GrantedAt, DateTimeOffset? RevokedAt, long Version);

public sealed record RequestedHostCapability(
    string CapabilityId, string CapabilityVersion, string SchemaHash, string SideEffectClass);

public sealed record RequestedHostResource(
    string ResourceId, string Type, string DisplayName, string Fingerprint, string State);

public sealed record HostCapabilityGrantRequest(string CapabilityId, string CapabilityVersion);

public sealed record HostResourceGrantRequest(string ResourceId, string AccessMode);

public sealed record HostClaim(
    P256PublicJwk PublicKey, string Protection, string Platform, string Architecture,
    string AgentVersion, string ProtocolVersion,
    IReadOnlyList<RequestedHostCapability> RequestedCapabilities,
    IReadOnlyList<RequestedHostResource> RequestedResources);

public sealed record HostPairing(
    string OwnerPrincipalId, string PairingId, string ClaimSecretHash, string State,
    int FailedClaims, int FailedConfirmations, HostClaim? RequestedHost,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? ClaimedAt,
    DateTimeOffset? ConfirmedAt, DateTimeOffset? CanceledAt, long Version);

public sealed record RemoteHost(
    string OwnerPrincipalId, string HostId, string DisplayName, string Platform,
    string Architecture, string Lifecycle, string ConnectionStatus, P256PublicJwk PublicKey,
    long KeyVersion, string Protection, string AgentVersion, string ProtocolVersion,
    long CapabilityCatalogVersion, long LastAcceptedSequence, DateTimeOffset? LastSeenAt,
    DateTimeOffset PairedAt, DateTimeOffset? RevokedAt, long Version);

public sealed record RemoteHostDetail(
    RemoteHost Host,
    IReadOnlyList<HostCapabilityAdvertisement> Capabilities,
    IReadOnlyList<HostCapabilityGrant> CapabilityGrants,
    IReadOnlyList<HostResource> Resources,
    IReadOnlyList<HostResourceGrant> ResourceGrants);

public sealed record HostPairingMutationSnapshot(
    string PairingId, string State, DateTimeOffset ExpiresAt, long Version);

public sealed record ResourceVersionSnapshot(long Version);

public sealed record RemoteHostSummarySnapshot(
    string HostId, string DisplayName, string Platform, string Architecture,
    string Lifecycle, string ConnectionStatus, string AgentVersion, string ProtocolVersion,
    DateTimeOffset? LastSeenAt, DateTimeOffset PairedAt, DateTimeOffset? RevokedAt, long Version);

public sealed record HostCapabilitySnapshot(
    string CapabilityId, string CapabilityVersion, string SchemaHash,
    string SideEffectClass, DateTimeOffset AdvertisedAt);

public sealed record HostCapabilityGrantSnapshot(
    string CapabilityId, string CapabilityVersion, DateTimeOffset GrantedAt,
    DateTimeOffset? RevokedAt, long Version);

public sealed record HostResourceSnapshot(
    string ResourceId, string Type, string DisplayName, string Fingerprint,
    string State, DateTimeOffset AdvertisedAt, long Version);

public sealed record HostResourceGrantSnapshot(
    string ResourceId, string AccessMode, DateTimeOffset GrantedAt,
    DateTimeOffset? RevokedAt, long Version);

public sealed record RemoteHostDetailSnapshot(
    RemoteHostSummarySnapshot Host,
    IReadOnlyList<HostCapabilitySnapshot> Capabilities,
    IReadOnlyList<HostCapabilityGrantSnapshot> CapabilityGrants,
    IReadOnlyList<HostResourceSnapshot> Resources,
    IReadOnlyList<HostResourceGrantSnapshot> ResourceGrants);

public sealed record RemoteHostProblemSnapshot(string Title, int Status, string Code);

public static class RemoteHostSnapshotSerializer
{
    private static readonly JsonSerializerOptions PublicJson = new(JsonSerializerDefaults.Web);

    public static string SerializePairing(HostPairing pairing)
        => JsonSerializer.Serialize(new HostPairingMutationSnapshot(
            pairing.PairingId, pairing.State, pairing.ExpiresAt, pairing.Version), PublicJson);

    public static string SerializeVersion(long version)
        => JsonSerializer.Serialize(new ResourceVersionSnapshot(version), PublicJson);

    public static string SerializeHost(RemoteHostDetail detail)
        => JsonSerializer.Serialize(ToSnapshot(detail), PublicJson);

    public static string SerializeProblem(int status, string code)
        => JsonSerializer.Serialize(new RemoteHostProblemSnapshot(code, status, code), PublicJson);

    public static RemoteHostDetailSnapshot ToSnapshot(RemoteHostDetail detail)
        => new(
            new(detail.Host.HostId, detail.Host.DisplayName, detail.Host.Platform,
                detail.Host.Architecture, detail.Host.Lifecycle, detail.Host.ConnectionStatus,
                detail.Host.AgentVersion, detail.Host.ProtocolVersion, detail.Host.LastSeenAt,
                detail.Host.PairedAt, detail.Host.RevokedAt, detail.Host.Version),
            detail.Capabilities.Select(item => new HostCapabilitySnapshot(
                item.CapabilityId, item.CapabilityVersion, item.SchemaHash,
                item.SideEffectClass, item.AdvertisedAt)).ToArray(),
            detail.CapabilityGrants.Select(item => new HostCapabilityGrantSnapshot(
                item.CapabilityId, item.CapabilityVersion, item.GrantedAt,
                item.RevokedAt, item.Version)).ToArray(),
            detail.Resources.Select(item => new HostResourceSnapshot(
                item.ResourceId, item.Type, item.DisplayName, item.Fingerprint,
                item.State, item.AdvertisedAt, item.Version)).ToArray(),
            detail.ResourceGrants.Select(item => new HostResourceGrantSnapshot(
                item.ResourceId, item.AccessMode, item.GrantedAt,
                item.RevokedAt, item.Version)).ToArray());
}

public static class RemoteHostValidation
{
    public const int MaximumGrants = 64;
    public const int MaximumClaimAttempts = 5;
    public const int MaximumConfirmationAttempts = 5;
    public const int MaximumArtifactsPerRun = 64;
    public const string SupportedCapabilityId = "host.repo.identity";
    public const string SupportedCapabilityVersion = "1";
    public const string SupportedProtocolVersion = "1";
    public const string SupportedPlatform = "macOS";
    public const string ReadOnly = "READ_ONLY";
    public const string Repository = "REPOSITORY";
    public const string Available = "AVAILABLE";
    public const int MaximumArtifactSummaryBytes = 512;
    public static readonly TimeSpan MaximumPairingTtl = TimeSpan.FromMinutes(5);

    private static readonly HashSet<string> JwkMembers = new(StringComparer.Ordinal)
    {
        "alg", "crv", "kty", "x", "y",
    };

    public static string CreateClaimSecret() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static string HashClaimSecret(string claimSecret)
        => Convert.ToHexString(SHA256.HashData(
            DecodeCanonicalBase64Url(claimSecret, 32, nameof(claimSecret)))).ToLowerInvariant();

    public static bool ClaimSecretMatches(string expectedHash, string claimSecret)
    {
        ValidateLowerHex(expectedHash, 64, nameof(expectedHash));
        byte[] actual;
        try
        {
            actual = Convert.FromHexString(HashClaimSecret(claimSecret));
        }
        catch (ArgumentException)
        {
            actual = SHA256.HashData(Encoding.ASCII.GetBytes("invalid-claim-secret"));
        }
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedHash), actual);
    }

    public static P256PublicJwk NormalizeP256PublicJwk(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 4,
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Public key JWK must be an object.", nameof(json));

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!JwkMembers.Contains(property.Name) || !values.TryAdd(property.Name, ReadString(property)))
                throw new ArgumentException("Public key JWK contains an unknown or duplicate member.", nameof(json));
        }

        if (values.Count is < 4 or > 5
            || !values.TryGetValue("kty", out var keyType) || keyType != "EC"
            || !values.TryGetValue("crv", out var curve) || curve != "P-256"
            || values.TryGetValue("alg", out var algorithm) && algorithm != "ES256"
            || !values.TryGetValue("x", out var x)
            || !values.TryGetValue("y", out var y))
        {
            throw new ArgumentException("Public key JWK is not a canonical P-256 key.", nameof(json));
        }

        var xBytes = DecodeCanonicalCoordinate(x, nameof(json));
        var yBytes = DecodeCanonicalCoordinate(y, nameof(json));
        try
        {
            using var key = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = xBytes, Y = yBytes },
            });
            _ = key.KeySize;
        }
        catch (CryptographicException exception)
        {
            throw new ArgumentException("Public key JWK point is not on P-256.", nameof(json), exception);
        }

        var canonicalJson = $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{x}\",\"y\":\"{y}\"}}";
        var thumbprint = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(canonicalJson)));
        return new P256PublicJwk(canonicalJson, x, y, thumbprint);
    }

    public static string DeriveConfirmationCode(string pairingId, P256PublicJwk publicKey)
    {
        ValidateIdentifier(pairingId, nameof(pairingId));
        ArgumentNullException.ThrowIfNull(publicKey);
        var thumbprint = DecodeCanonicalBase64Url(publicKey.Thumbprint, 32, nameof(publicKey));
        var pairingBytes = Encoding.ASCII.GetBytes(pairingId);
        var input = new byte[pairingBytes.Length + 1 + thumbprint.Length];
        pairingBytes.CopyTo(input, 0);
        thumbprint.CopyTo(input, pairingBytes.Length + 1);
        var digest = SHA256.HashData(input);
        var value = ((uint)digest[0] << 24) | ((uint)digest[1] << 16) | ((uint)digest[2] << 8) | digest[3];
        return (value % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static void ValidateClaim(HostClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var normalizedKey = NormalizeP256PublicJwk(claim.PublicKey.CanonicalJson);
        if (normalizedKey != claim.PublicKey)
            throw new ArgumentException("Public key JWK is not normalized.", nameof(claim));
        if (claim.Protection is not ("SECURE_ENCLAVE" or "KEYCHAIN_THIS_DEVICE_ONLY"))
            throw new ArgumentException("Host key protection is not supported.", nameof(claim));
        if (claim.Platform != SupportedPlatform)
            throw new ArgumentException("Host platform is not supported.", nameof(claim));
        if (claim.Architecture is not ("arm64" or "x86_64"))
            throw new ArgumentException("Host architecture is not supported.", nameof(claim));
        ValidatePrintable(claim.AgentVersion, nameof(claim.AgentVersion));
        if (claim.ProtocolVersion != SupportedProtocolVersion)
            throw new ArgumentException("Host protocol version is not supported.", nameof(claim));
        ValidateGrantCount(claim.RequestedCapabilities.Count, nameof(claim.RequestedCapabilities));
        ValidateGrantCount(claim.RequestedResources.Count, nameof(claim.RequestedResources));
        EnsureUnique(claim.RequestedCapabilities.Select(item => $"{item.CapabilityId}\n{item.CapabilityVersion}"), nameof(claim.RequestedCapabilities));
        EnsureUnique(claim.RequestedResources.Select(item => item.ResourceId), nameof(claim.RequestedResources));
        foreach (var capability in claim.RequestedCapabilities)
        {
            if (capability.CapabilityId != SupportedCapabilityId
                || capability.CapabilityVersion != SupportedCapabilityVersion)
                throw new ArgumentException("Host capability is not supported.", nameof(claim));
            ValidateLowerHex(capability.SchemaHash, 64, nameof(capability.SchemaHash));
            if (capability.SideEffectClass != ReadOnly)
                throw new ArgumentException("Host capability effect is not supported.", nameof(claim));
        }
        foreach (var resource in claim.RequestedResources)
        {
            ValidateIdentifier(resource.ResourceId, nameof(resource.ResourceId));
            if (resource.Type != Repository)
                throw new ArgumentException("Host resource type is not supported.", nameof(claim));
            ValidatePrintable(resource.DisplayName, nameof(resource.DisplayName));
            ValidateLowerHex(resource.Fingerprint, 64, nameof(resource.Fingerprint));
            if (resource.State != Available)
                throw new ArgumentException("Host resource state is not supported.", nameof(claim));
        }
    }

    public static void ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64 || value[0] is < 'a' or > 'z' && value[0] is < '0' or > '9')
            throw new ArgumentException("Identifier is not canonical.", parameterName);
        foreach (var character in value)
        {
            if (character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-')
                throw new ArgumentException("Identifier is not canonical.", parameterName);
        }
    }

    public static void ValidateLowerHex(string value, int length, string parameterName)
    {
        if (value.Length != length || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Value is not canonical lowercase hexadecimal.", parameterName);
    }

    public static void ValidatePrintableText(string value, string parameterName)
        => ValidatePrintable(value, parameterName);

    public static void ValidateActionHostBinding(
        string? hostId,
        string? hostLeaseId,
        string? hostResourceGrantHash)
    {
        var any = hostId is not null || hostLeaseId is not null || hostResourceGrantHash is not null;
        var all = hostId is not null && hostLeaseId is not null && hostResourceGrantHash is not null;
        if (any != all)
            throw new ArgumentException("Host-backed bindings require host, lease, and resource hash together.");
        if (!all)
            return;

        ValidateIdentifier(hostId!, nameof(hostId));
        ValidateIdentifier(hostLeaseId!, nameof(hostLeaseId));
        ValidateLowerHex(hostResourceGrantHash!, 64, nameof(hostResourceGrantHash));
    }

    public static void ValidateExecutionPolicy(
        string location,
        string? preferredHostId,
        IReadOnlyList<(string Id, string Version)> requiredCapabilities,
        IReadOnlyList<string> requiredResourceIds,
        string fallbackPolicy)
    {
        if (!JobExecutionLocations.IsValid(location))
            throw new ArgumentException("Execution location is not supported.", nameof(location));
        if (!JobExecutionFallbackPolicies.IsValid(fallbackPolicy))
            throw new ArgumentException("Execution fallback policy is not supported.", nameof(fallbackPolicy));
        if (preferredHostId is not null)
            ValidateIdentifier(preferredHostId, nameof(preferredHostId));

        ValidateGrantCount(requiredCapabilities.Count, nameof(requiredCapabilities));
        ValidateGrantCount(requiredResourceIds.Count, nameof(requiredResourceIds));
        EnsureUnique(requiredCapabilities.Select(item => $"{item.Id}\n{item.Version}"), nameof(requiredCapabilities));
        EnsureUnique(requiredResourceIds, nameof(requiredResourceIds));

        foreach (var capability in requiredCapabilities)
        {
            if (capability.Id != SupportedCapabilityId
                || capability.Version != SupportedCapabilityVersion)
            {
                throw new ArgumentException("Execution capability is not supported.", nameof(requiredCapabilities));
            }
        }

        foreach (var resourceId in requiredResourceIds)
            ValidateIdentifier(resourceId, nameof(requiredResourceIds));

        if (location == JobExecutionLocations.Server)
        {
            if (preferredHostId is not null || requiredCapabilities.Count != 0 || requiredResourceIds.Count != 0)
                throw new ArgumentException("Server execution cannot carry Host requirements.", nameof(location));
            return;
        }

        if (requiredCapabilities.Count != 1)
            throw new ArgumentException("Host execution requires exactly one proof capability.", nameof(requiredCapabilities));
        if (requiredResourceIds.Count < 1)
            throw new ArgumentException("Host execution requires at least one repository resource.", nameof(requiredResourceIds));
        if (location == JobExecutionLocations.Host && preferredHostId is null)
            throw new ArgumentException("Explicit Host execution requires a preferred Host ID.", nameof(preferredHostId));
        if (location == JobExecutionLocations.AnyCompatibleHost && preferredHostId is not null)
            throw new ArgumentException("Compatible Host execution cannot pin a preferred Host ID.", nameof(preferredHostId));
    }

    public static string ComputeHostResourceGrantHash(
        IReadOnlyList<HostResourceGrantTuple> tuples)
    {
        ArgumentNullException.ThrowIfNull(tuples);
        ValidateGrantCount(tuples.Count, nameof(tuples));
        var builder = new StringBuilder();
        string? previousResourceId = null;
        foreach (var tuple in tuples)
        {
            ArgumentNullException.ThrowIfNull(tuple);
            ValidateIdentifier(tuple.ResourceId, nameof(tuples));
            if (tuple.ResourceGrantVersion < 1)
                throw new ArgumentOutOfRangeException(nameof(tuples), "Grant version must be a positive Int64.");
            if (tuple.AccessMode != ReadOnly)
                throw new ArgumentException("Resource access mode is not supported.", nameof(tuples));
            ValidateLowerHex(tuple.Fingerprint, 64, nameof(tuples));
            if (previousResourceId is not null
                && string.CompareOrdinal(previousResourceId, tuple.ResourceId) >= 0)
            {
                throw new ArgumentException("Resource tuples must be unique and ASCII-sorted by resource ID.", nameof(tuples));
            }

            previousResourceId = tuple.ResourceId;
            builder.Append(tuple.ResourceId)
                .Append('\n')
                .Append(tuple.ResourceGrantVersion.ToString(CultureInfo.InvariantCulture))
                .Append('\n')
                .Append(tuple.AccessMode)
                .Append('\n')
                .Append(tuple.Fingerprint)
                .Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string ReadString(JsonProperty property)
    {
        if (property.Value.ValueKind != JsonValueKind.String)
            throw new ArgumentException("Public key JWK members must be strings.");
        return property.Value.GetString()!;
    }

    private static byte[] DecodeCanonicalCoordinate(string value, string parameterName)
        => DecodeCanonicalBase64Url(value, 32, parameterName);

    private static byte[] DecodeCanonicalBase64Url(string value, int expectedLength, string parameterName)
    {
        if (value.Contains('=', StringComparison.Ordinal) || value.Any(character =>
                character is not (>= 'A' and <= 'Z')
                    and not (>= 'a' and <= 'z')
                    and not (>= '0' and <= '9')
                    and not '-' and not '_'))
            throw new ArgumentException("Value is not canonical base64url.", parameterName);
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Value is not canonical base64url.", parameterName, exception);
        }
        if (decoded.Length != expectedLength || Base64UrlEncode(decoded) != value)
            throw new ArgumentException("Value is not canonical base64url.", parameterName);
        return decoded;
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void ValidatePrintable(string value, string parameterName)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || value.Any(character => character is < ' ' or > '~'))
            throw new ArgumentException("Value must be bounded printable ASCII.", parameterName);
    }

    private static void ValidateGrantCount(int count, string parameterName)
    {
        if (count > MaximumGrants)
            throw new ArgumentOutOfRangeException(parameterName);
    }

    private static void EnsureUnique(IEnumerable<string> values, string parameterName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (values.Any(value => !seen.Add(value)))
            throw new ArgumentException("Duplicate requested grant.", parameterName);
    }
}