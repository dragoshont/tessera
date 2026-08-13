using System.Security.Cryptography;
using System.Text.Json;
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

        Assert.All(new[] { "poll", "lease-ack", "lease-events", "lease-complete", "lease-reconcile" },
            operation => Assert.True(HostAcceptedMessageOperations.IsValid(operation)));
        Assert.False(HostAcceptedMessageOperations.IsValid("shell"));
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