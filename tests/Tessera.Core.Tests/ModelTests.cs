using Tessera.Core.Identity;
using Tessera.Core.Model;
using Xunit;

namespace Tessera.Core.Tests;

public sealed class ModelTests
{
    [Theory]
    [InlineData(VerificationMethod.Mtls, true)]
    [InlineData(VerificationMethod.SpiffeSvid, true)]
    [InlineData(VerificationMethod.OidcJwt, true)]
    [InlineData(VerificationMethod.Dev, false)]
    public void VerificationMethod_IsVerified_only_for_cryptographic_methods(VerificationMethod method, bool expected)
    {
        Assert.Equal(expected, method.IsVerified());
    }

    [Fact]
    public void CallerIdentity_reports_verification_from_method()
    {
        Assert.True(TestData.VerifiedCaller().IsVerified);
        Assert.False(TestData.UnverifiedCaller().IsVerified);
    }

    [Fact]
    public void EndUserAssertion_defaults_to_verified_oidc()
    {
        var user = new EndUserAssertion("oid-123", "https://issuer/v2.0");
        Assert.True(user.IsVerified);
        Assert.Equal(VerificationMethod.OidcJwt, user.VerifiedVia);
    }

    [Fact]
    public void Decision_factory_helpers_set_effect_and_reason()
    {
        var allow = Decision.Allow("granted");
        var deny = Decision.Deny("nope");
        var stepUp = Decision.StepUp("confirm first", "write:pay");

        Assert.True(allow.Allowed);
        Assert.Equal(Effect.Allow, allow.Effect);

        Assert.False(deny.Allowed);
        Assert.Equal(Effect.Deny, deny.Effect);

        Assert.Equal(Effect.StepUp, stepUp.Effect);
        Assert.False(stepUp.Allowed);
        Assert.Equal("write:pay", stepUp.Obligations["step_up"]);
    }

    [Fact]
    public void AccessRequest_defaults_to_no_delegation()
    {
        var request = TestData.Request();
        Assert.Null(request.OnBehalfOf);
    }
}
