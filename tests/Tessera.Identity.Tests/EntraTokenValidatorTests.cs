using Tessera.Core.Identity;
using Xunit;

namespace Tessera.Identity.Tests;

public sealed class EntraTokenValidatorTests
{
    private static EntraTokenValidator Validator(TokenFactory factory, string? audience = TokenFactory.Audience, string tenantId = TokenFactory.TenantId)
    {
        var options = new OidcValidationOptions
        {
            Issuer = TokenFactory.Issuer,
            Audience = audience ?? "",
            TenantId = tenantId,
        };
        return new EntraTokenValidator(factory.ConfigurationManager(), options);
    }

    [Fact]
    public async Task Valid_user_token_yields_a_verified_end_user_assertion()
    {
        var factory = new TokenFactory();
        var result = await Validator(factory).ValidateAsync(factory.UserToken());

        Assert.True(result.Succeeded);
        Assert.False(result.IsAppOnly);

        var user = result.ToEndUserAssertion();
        Assert.NotNull(user);
        Assert.Equal("oid-alice", user!.Subject);
        Assert.Equal("alice@example.com", user.PreferredUsername);
        Assert.Equal(VerificationMethod.OidcJwt, user.VerifiedVia);
        Assert.True(user.IsVerified);
    }

    [Fact]
    public async Task Valid_app_only_token_yields_a_verified_caller_identity()
    {
        var factory = new TokenFactory();
        var result = await Validator(factory).ValidateAsync(factory.AppOnlyToken("app-crawler-9999"));

        Assert.True(result.Succeeded);
        Assert.True(result.IsAppOnly);
        Assert.Null(result.ToEndUserAssertion());

        var caller = result.ToCallerIdentity();
        Assert.NotNull(caller);
        Assert.Equal("app-crawler-9999", caller!.Id);
        Assert.True(caller.IsVerified);
    }

    [Fact]
    public async Task Wrong_audience_is_rejected()
    {
        var factory = new TokenFactory();
        var result = await Validator(factory).ValidateAsync(factory.UserToken(audience: "some-other-app"));
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Wrong_issuer_is_rejected()
    {
        var factory = new TokenFactory();
        var result = await Validator(factory).ValidateAsync(
            factory.UserToken(issuer: "https://login.microsoftonline.com/evil/v2.0"));
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        var factory = new TokenFactory();
        var result = await Validator(factory).ValidateAsync(
            factory.UserToken(expires: DateTime.UtcNow.AddMinutes(-30)));
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Wrong_tenant_is_rejected()
    {
        var factory = new TokenFactory();
        var result = await Validator(factory).ValidateAsync(factory.UserToken(tenantId: "another-tenant"));
        Assert.False(result.Succeeded);
        Assert.Contains("tenant", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Token_signed_by_an_untrusted_key_is_rejected()
    {
        // Validate a token minted by a DIFFERENT factory (different signing key).
        var trusted = new TokenFactory();
        var attacker = TokenFactory.Untrusted();
        var result = await Validator(trusted).ValidateAsync(attacker.UserToken());
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Fails_closed_when_no_audience_is_configured()
    {
        var factory = new TokenFactory();
        var validator = Validator(factory, audience: null); // no audience => delegation off
        Assert.False(validator.DelegationEnabled);

        var result = await validator.ValidateAsync(factory.UserToken());
        Assert.False(result.Succeeded);
        Assert.Contains("fail-closed", result.FailureReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_token_is_rejected()
    {
        var factory = new TokenFactory();
        var result = await Validator(factory).ValidateAsync("");
        Assert.False(result.Succeeded);
    }

    // ── Multi-tenant (/common) issuer validation ──────────────────────────────

    // Generic test tenants (NOT real). The consumer-tenant constant below is the
    // documented, public Microsoft personal-account tenant — not anyone's data.
    private const string WorkforceTenant = "11111111-1111-1111-1111-111111111111";
    private const string ConsumerTenant = "9188040d-6c67-4c5b-b112-36a304b66dad";

    private static EntraTokenValidator MultiTenantValidator(TokenFactory factory, params string[] allowedTenants)
    {
        var options = new OidcValidationOptions
        {
            Issuer = "https://login.microsoftonline.com/common/v2.0",
            Audience = TokenFactory.Audience,
            AllowedTenants = allowedTenants,
        };
        return new EntraTokenValidator(factory.ConfigurationManager(), options);
    }

    [Fact]
    public async Task MultiTenant_accepts_a_token_whose_iss_matches_its_tid()
    {
        var factory = new TokenFactory();
        var result = await MultiTenantValidator(factory).ValidateAsync(
            factory.TenantToken(WorkforceTenant, preferredUsername: "alice@example.com"));

        Assert.True(result.Succeeded);
        Assert.Equal("alice@example.com", result.ToEndUserAssertion()!.PreferredUsername);
    }

    [Fact]
    public async Task MultiTenant_accepts_a_personal_msa_consumer_tenant_token()
    {
        var factory = new TokenFactory();
        var result = await MultiTenantValidator(factory).ValidateAsync(
            factory.TenantToken(ConsumerTenant, preferredUsername: "bob@example.com"));
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task MultiTenant_rejects_a_spoofed_issuer_not_matching_its_tid()
    {
        var factory = new TokenFactory();
        // tid says tenant-B, but iss claims tenant-A → reject.
        var result = await MultiTenantValidator(factory).ValidateAsync(
            factory.TenantToken("tenant-B", spoofIssuer: "https://login.microsoftonline.com/tenant-A/v2.0"));
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task MultiTenant_allow_list_blocks_unlisted_tenants()
    {
        var factory = new TokenFactory();
        var validator = MultiTenantValidator(factory, WorkforceTenant);

        Assert.True((await validator.ValidateAsync(factory.TenantToken(WorkforceTenant))).Succeeded);
        Assert.False((await validator.ValidateAsync(factory.TenantToken("some-other-tenant"))).Succeeded);
    }
}
