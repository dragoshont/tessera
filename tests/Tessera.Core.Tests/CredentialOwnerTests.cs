using Tessera.Core.Resolution;
using Xunit;

namespace Tessera.Core.Tests;

public sealed class CredentialOwnerTests
{
    [Fact]
    public void Target_binding_defaults_to_service_owned()
    {
        // The fail-safe default: a credential nobody named is treated as a brokered
        // service key — never revealed to anyone (ADR 0020).
        var binding = new TargetBinding("media", "media-key");
        Assert.Equal(CredentialOwner.Service, binding.Owner);
        Assert.Null(binding.Guardian);
    }

    [Theory]
    [InlineData("user", CredentialOwner.User)]
    [InlineData("USER", CredentialOwner.User)]
    [InlineData("dependent", CredentialOwner.Dependent)]
    [InlineData("service", CredentialOwner.Service)]
    public void Parse_maps_known_tokens(string token, CredentialOwner expected) =>
        Assert.Equal(expected, CredentialOwners.Parse(token));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("nonsense")]
    public void Parse_is_fail_safe_service_for_unknown(string? token) =>
        // An unknown/empty owner is never silently treated as user-owned.
        Assert.Equal(CredentialOwner.Service, CredentialOwners.Parse(token));

    [Fact]
    public void ToToken_round_trips()
    {
        Assert.Equal("user", CredentialOwners.ToToken(CredentialOwner.User));
        Assert.Equal("dependent", CredentialOwners.ToToken(CredentialOwner.Dependent));
        Assert.Equal("service", CredentialOwners.ToToken(CredentialOwner.Service));
    }

    [Fact]
    public void A_dependent_binding_carries_its_guardian()
    {
        var binding = new TargetBinding("health-portal", "hp-kid", "kid@example.com", CredentialOwner.Dependent, "alice@example.com");
        Assert.Equal(CredentialOwner.Dependent, binding.Owner);
        Assert.Equal("alice@example.com", binding.Guardian);
    }
}
