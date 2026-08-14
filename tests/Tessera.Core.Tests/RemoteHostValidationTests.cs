using System.Security.Cryptography;
using System.Text.Json;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Xunit;

namespace Tessera.Core.Tests;

public sealed class RemoteHostValidationTests
{
    [Fact]
    public void P256_jwk_is_normalized_thumbprinted_and_used_for_confirmation_code()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(false);
        var x = Base64Url(parameters.Q.X!);
        var y = Base64Url(parameters.Q.Y!);

        var normalized = RemoteHostValidation.NormalizeP256PublicJwk(
            $$"""{"y":"{{y}}","alg":"ES256","x":"{{x}}","crv":"P-256","kty":"EC"}""");

        Assert.Equal($$"""{"crv":"P-256","kty":"EC","x":"{{x}}","y":"{{y}}"}""", normalized.CanonicalJson);
        Assert.Equal(43, normalized.Thumbprint.Length);
        Assert.Matches("^[0-9]{6}$", RemoteHostValidation.DeriveConfirmationCode("pairing-1", normalized));
        Assert.Equal(
            RemoteHostValidation.DeriveConfirmationCode("pairing-1", normalized),
            RemoteHostValidation.DeriveConfirmationCode("pairing-1", normalized));
    }

    [Theory]
    [MemberData(nameof(InvalidJwks))]
    public void P256_jwk_rejects_noncanonical_private_unknown_duplicate_and_off_curve_values(string jwk)
        => Assert.Throws<ArgumentException>(() => RemoteHostValidation.NormalizeP256PublicJwk(jwk));

    [Fact]
    public void Claim_rejects_duplicate_and_oversized_grants()
    {
        var key = ValidKey();
        var capability = new RequestedHostCapability(
            "host.repo.identity", "1", new string('a', 64), "READ_ONLY");
        var duplicate = new HostClaim(key, "KEYCHAIN_THIS_DEVICE_ONLY", "macOS", "arm64", "1", "1", [capability, capability], []);
        Assert.Throws<ArgumentException>(() => RemoteHostValidation.ValidateClaim(duplicate));

        var resources = Enumerable.Range(0, 65).Select(index => new RequestedHostResource(
            $"resource-{index}", "REPOSITORY", $"Repository {index}", new string('a', 64), "AVAILABLE")).ToArray();
        var oversized = new HostClaim(key, "KEYCHAIN_THIS_DEVICE_ONLY", "macOS", "arm64", "1", "1", [], resources);
        Assert.Throws<ArgumentOutOfRangeException>(() => RemoteHostValidation.ValidateClaim(oversized));
    }

    [Fact]
    public void Claim_and_message_operations_reject_values_outside_the_v18_proof_slice()
    {
        var valid = new HostClaim(ValidKey(), "KEYCHAIN_THIS_DEVICE_ONLY", "macOS", "arm64", "1.0.0", "1",
            [new("host.repo.identity", "1", new string('a', 64), "READ_ONLY")],
            [new("repo-main", "REPOSITORY", "Repo", new string('b', 64), "AVAILABLE")]);
        RemoteHostValidation.ValidateClaim(valid);

        var invalid = new[]
        {
            valid with { Protection = "FILE" },
            valid with { Platform = "Linux" },
            valid with { Architecture = "x64" },
            valid with { ProtocolVersion = "2" },
            valid with { RequestedCapabilities = [valid.RequestedCapabilities[0] with { CapabilityId = "host.shell" }] },
            valid with { RequestedCapabilities = [valid.RequestedCapabilities[0] with { CapabilityVersion = "2" }] },
            valid with { RequestedCapabilities = [valid.RequestedCapabilities[0] with { SchemaHash = new string('g', 64) }] },
            valid with { RequestedCapabilities = [valid.RequestedCapabilities[0] with { SideEffectClass = "WRITE" }] },
            valid with { RequestedResources = [valid.RequestedResources[0] with { Type = "DIRECTORY" }] },
            valid with { RequestedResources = [valid.RequestedResources[0] with { Fingerprint = new string('g', 64) }] },
            valid with { RequestedResources = [valid.RequestedResources[0] with { State = "MISSING" }] },
        };
        Assert.All(invalid, claim => Assert.Throws<ArgumentException>(
            () => RemoteHostValidation.ValidateClaim(claim)));

        Assert.All(new[] { "poll", "lease-ack", "lease-events", "lease-complete", "lease-reconcile", "lease-artifact" },
            operation => Assert.True(HostAcceptedMessageOperations.IsValid(operation)));
        Assert.False(HostAcceptedMessageOperations.IsValid("shell"));
    }

    [Fact]
    public void Resource_grant_tuple_hash_matches_fixed_vectors_and_rejects_noncanonical_input()
    {
        Assert.Equal(
            "20979ecfc8db5ebf85f38af6751492dce85644f3bfff79e895a0c44bccda0a22",
            RemoteHostValidation.ComputeHostResourceGrantHash(
            [
                new HostResourceGrantTuple(
                    "repo-a",
                    1,
                    "READ_ONLY",
                    new string('a', 64)),
            ]));

        Assert.Equal(
            "9198a3189913316bd3f1e6e05add6ee8f23f959184a30cef79ccae75dccd2e0e",
            RemoteHostValidation.ComputeHostResourceGrantHash(
            [
                new HostResourceGrantTuple(
                    "repo-a",
                    2,
                    "READ_ONLY",
                    new string('b', 64)),
                new HostResourceGrantTuple(
                    "repo-b",
                    7,
                    "READ_ONLY",
                    new string('c', 64)),
            ]));

        Assert.Throws<ArgumentException>(() => RemoteHostValidation.ComputeHostResourceGrantHash(
        [
            new HostResourceGrantTuple("repo-b", 1, "READ_ONLY", new string('a', 64)),
            new HostResourceGrantTuple("repo-a", 1, "READ_ONLY", new string('b', 64)),
        ]));
        Assert.Throws<ArgumentException>(() => RemoteHostValidation.ComputeHostResourceGrantHash(
        [
            new HostResourceGrantTuple("repo-a", 1, "READ_ONLY", new string('a', 64)),
            new HostResourceGrantTuple("repo-a", 2, "READ_ONLY", new string('b', 64)),
        ]));
        Assert.Throws<ArgumentOutOfRangeException>(() => RemoteHostValidation.ComputeHostResourceGrantHash(
        [
            new HostResourceGrantTuple("repo-a", 0, "READ_ONLY", new string('a', 64)),
        ]));
    }

    [Fact]
    public void Action_r2_host_binding_requires_the_full_host_tuple_or_none()
    {
        var noHostBinding = new ActionR2Binding(
            "account-1",
            "plugin-id",
            "1.0.0",
            new string('d', 64),
            DateTimeOffset.UtcNow.AddMinutes(5),
            "execution-1");
        Assert.Null(noHostBinding.HostId);

        var hostBinding = new ActionR2Binding(
            "account-1",
            "plugin-id",
            "1.0.0",
            new string('d', 64),
            DateTimeOffset.UtcNow.AddMinutes(5),
            "execution-1",
            hostId: "host-main",
            hostLeaseId: "lease-main",
            hostResourceGrantHash: new string('e', 64));
        Assert.Equal("host-main", hostBinding.HostId);
        Assert.Equal("lease-main", hostBinding.HostLeaseId);
        Assert.Equal(new string('e', 64), hostBinding.HostResourceGrantHash);

        Assert.Throws<ArgumentException>(() => new ActionR2Binding(
            "account-1",
            "plugin-id",
            "1.0.0",
            new string('d', 64),
            DateTimeOffset.UtcNow.AddMinutes(5),
            "execution-1",
            hostId: "host-main"));
    }

    [Fact]
    public void Execution_policy_shapes_are_exact_for_server_explicit_and_compatible_host()
    {
        RemoteHostValidation.ValidateExecutionPolicy("SERVER", null, [], [], "NONE");
        RemoteHostValidation.ValidateExecutionPolicy(
            "HOST", "host-main", [("host.repo.identity", "1")], ["repo-main"], "NONE");
        RemoteHostValidation.ValidateExecutionPolicy(
            "ANY_COMPATIBLE_HOST", null, [("host.repo.identity", "1")], ["repo-main"], "NONE");

        Assert.Throws<ArgumentException>(() => RemoteHostValidation.ValidateExecutionPolicy(
            "SERVER", null, [("host.repo.identity", "1")], [], "NONE"));
        Assert.Throws<ArgumentException>(() => RemoteHostValidation.ValidateExecutionPolicy(
            "HOST", null, [("host.repo.identity", "1")], ["repo-main"], "NONE"));
        Assert.Throws<ArgumentException>(() => RemoteHostValidation.ValidateExecutionPolicy(
            "HOST", "host-main", [("host.repo.identity", "1")], [], "NONE"));
        Assert.Throws<ArgumentException>(() => RemoteHostValidation.ValidateExecutionPolicy(
            "ANY_COMPATIBLE_HOST", "host-main", [("host.repo.identity", "1")], ["repo-main"], "NONE"));
        Assert.Throws<ArgumentException>(() => RemoteHostValidation.ValidateExecutionPolicy(
            "ANY_COMPATIBLE_HOST", null, [("host.shell", "1")], ["repo-main"], "NONE"));
    }

    [Fact]
    public void Remote_host_output_normalization_redaction_truncation_and_hash_match_fixed_vectors()
    {
        var normalized = RemoteHostOutputNormalizer.Normalize(
            System.Text.Encoding.UTF8.GetBytes("line1\r\nAuthorization: Bearer canary-token\u0000\rline2"));
        Assert.Equal("line1\nAuthorization: Bearer [REDACTED]\nline2", normalized.Text);
        Assert.True(normalized.Redacted);
        Assert.False(normalized.Truncated);
        Assert.Equal(44, normalized.SizeBytes);
        Assert.Equal("69a9a706525148f438e43498efcbfa2ed5984a421b0ff91ec434ebd96587340c", normalized.Sha256);

        var structured = RemoteHostOutputNormalizer.Normalize(
            System.Text.Encoding.UTF8.GetBytes("{\"password\":\"json-canary\",\"path\":\"/Volumes/External/repo\",\"note\":\"safe\"}\nmount /Volumes/External/repo"));
        Assert.Equal("{\"password\":\"[REDACTED]\",\"path\":\"[REDACTED]\",\"note\":\"safe\"}\nmount [REDACTED]", structured.Text);
        Assert.True(structured.Redacted);
        Assert.DoesNotContain("json-canary", structured.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("/Volumes/", structured.Text, StringComparison.Ordinal);

        var escapedStructured = RemoteHostOutputNormalizer.Normalize(
            System.Text.Encoding.UTF8.GetBytes("{\"password\":\"do\\\"not-leak\",\"secret_note\":\"don't-leak\"}"));
        Assert.Equal("{\"password\":\"[REDACTED]\",\"secret_note\":\"[REDACTED]\"}", escapedStructured.Text);
        Assert.DoesNotContain("not-leak", escapedStructured.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("don't-leak", escapedStructured.Text, StringComparison.Ordinal);

        var bareStructured = RemoteHostOutputNormalizer.Normalize(
            System.Text.Encoding.UTF8.GetBytes("password: \"bare secret with spaces\"\nroot /System/Volumes/Data/repo"));
        Assert.Equal("password: [REDACTED]\nroot [REDACTED]", bareStructured.Text);
        Assert.DoesNotContain("bare secret", bareStructured.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("/System/Volumes/", bareStructured.Text, StringComparison.Ordinal);

        var keyMaterial = RemoteHostOutputNormalizer.Normalize(
            System.Text.Encoding.UTF8.GetBytes("{\"privateKey\":\"camel-key-canary\",\"private_key\":\"snake-key-canary\",\"binaryPath\":\"/usr/local/bin/tool\"}"));
        Assert.DoesNotContain("camel-key-canary", keyMaterial.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("snake-key-canary", keyMaterial.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("/usr/local/", keyMaterial.Text, StringComparison.Ordinal);

        var truncated = RemoteHostOutputNormalizer.Normalize(
            System.Text.Encoding.UTF8.GetBytes("ééé"), 5);
        Assert.Equal("éé", truncated.Text);
        Assert.False(truncated.Redacted);
        Assert.True(truncated.Truncated);
        Assert.Equal(4, truncated.SizeBytes);
        Assert.Equal("f13c007a1d8e6e1300b5957a143810cdd3555825466cf5d2617b1ac2fd8bd76b", truncated.Sha256);

        var artifactVector = RemoteHostOutputNormalizer.Normalize(
            System.Text.Encoding.UTF8.GetBytes(new string('a', 262145)),
            RemoteHostProtocol.MaximumArtifactBodyBytes);
        Assert.Equal(RemoteHostProtocol.MaximumArtifactBodyBytes, System.Text.Encoding.UTF8.GetByteCount(artifactVector.Text));
        Assert.Equal(RemoteHostProtocol.MaximumArtifactBodyBytes, artifactVector.SizeBytes);
        Assert.True(artifactVector.Truncated);

        var pemPrefix = "-----BEGIN " + "PRIVATE" + " KEY-----";
        var incompletePem = RemoteHostOutputNormalizer.Normalize(
            System.Text.Encoding.UTF8.GetBytes(pemPrefix + new string('x', RemoteHostProtocol.MaximumArtifactBodyBytes - pemPrefix.Length)),
            RemoteHostProtocol.MaximumArtifactBodyBytes);
        Assert.Equal("[REDACTED]", incompletePem.Text);
        Assert.True(incompletePem.Redacted);
        Assert.False(incompletePem.Truncated);

        var escapedQuoteStream = string.Concat(Enumerable.Repeat(
            "\\\"",
            RemoteHostProtocol.MaximumArtifactBodyBytes / 2));
        var escapedQuoteVector = RemoteHostOutputNormalizer.Normalize(
            System.Text.Encoding.UTF8.GetBytes(escapedQuoteStream),
            RemoteHostProtocol.MaximumArtifactBodyBytes);
        Assert.Equal(RemoteHostProtocol.MaximumArtifactBodyBytes, escapedQuoteVector.SizeBytes);
        Assert.False(escapedQuoteVector.Truncated);

        Assert.Throws<System.Text.DecoderFallbackException>(() =>
            RemoteHostOutputNormalizer.Normalize([0xff]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RemoteHostOutputNormalizer.Normalize(System.Text.Encoding.UTF8.GetBytes("x"), RemoteHostProtocol.MaximumArtifactBodyBytes + 1));
    }

    public static TheoryData<string> InvalidJwks()
    {
        var valid = ValidKey();
        using var document = JsonDocument.Parse(valid.CanonicalJson);
        var x = document.RootElement.GetProperty("x").GetString()!;
        var y = document.RootElement.GetProperty("y").GetString()!;
        return new TheoryData<string>
        {
            $$"""{"kty":"EC","crv":"P-256","x":"{{x}}=","y":"{{y}}"}""",
            $$"""{"kty":"EC","crv":"P-256","x":"{{x}}","y":"{{y}}","d":"private-canary"}""",
            $$"""{"kty":"EC","kty":"EC","crv":"P-256","x":"{{x}}","y":"{{y}}"}""",
            $$"""{"kty":"EC","crv":"P-256","x":"{{x}}","y":"{{y}}","use":"sig"}""",
            $$"""{"kty":"EC","crv":"P-256","alg":"ES384","x":"{{x}}","y":"{{y}}"}""",
            $$"""{"kty":"EC","crv":"P-384","x":"{{x}}","y":"{{y}}"}""",
            $$"""{"kty":"EC","crv":"P-256","x":"{{Base64Url(new byte[32])}}","y":"{{Base64Url(new byte[32])}}"}""",
        };
    }

    private static P256PublicJwk ValidKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(false);
        return RemoteHostValidation.NormalizeP256PublicJwk(
            $$"""{"kty":"EC","crv":"P-256","x":"{{Base64Url(parameters.Q.X!)}}","y":"{{Base64Url(parameters.Q.Y!)}}"}""");
    }

    private static string Base64Url(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}