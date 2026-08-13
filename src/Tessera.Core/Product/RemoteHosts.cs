using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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

    public static bool IsValid(string value)
        => value is Poll or LeaseAck or LeaseEvents or LeaseComplete or LeaseReconcile;
}

public sealed record P256PublicJwk(string CanonicalJson, string X, string Y, string Thumbprint);

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
    public const string SupportedCapabilityId = "host.repo.identity";
    public const string SupportedCapabilityVersion = "1";
    public const string SupportedProtocolVersion = "1";
    public const string SupportedPlatform = "macOS";
    public const string ReadOnly = "READ_ONLY";
    public const string Repository = "REPOSITORY";
    public const string Available = "AVAILABLE";
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