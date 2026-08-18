using Xunit;

namespace Tessera.Identity.Tests;

public sealed class CompositeTokenValidatorTests
{
    [Fact]
    public async Task Accepts_a_token_from_exactly_one_trust_lane()
    {
        var accepted = TesseraTokenResult.Success(new Dictionary<string, string>
        {
            ["sub"] = "owner",
            ["iss"] = "https://auth.example/application/o/librechat/",
            ["aud"] = "librechat",
        });
        var validator = new CompositeTokenValidator(
            [new FakeValidator(TesseraTokenResult.Fail("wrong lane")), new FakeValidator(accepted)]);

        var result = await validator.ValidateAsync("token");

        Assert.Same(accepted, result);
    }

    [Fact]
    public async Task Rejects_a_token_accepted_by_overlapping_trust_lanes()
    {
        var accepted = TesseraTokenResult.Success(new Dictionary<string, string> { ["sub"] = "owner" });
        var validator = new CompositeTokenValidator(
            [new FakeValidator(accepted), new FakeValidator(accepted)]);

        var result = await validator.ValidateAsync("token");

        Assert.False(result.Succeeded);
        Assert.Contains("ambiguous", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejects_when_every_trust_lane_rejects_the_token()
    {
        var validator = new CompositeTokenValidator(
            [new FakeValidator(TesseraTokenResult.Fail("one")), new FakeValidator(TesseraTokenResult.Fail("two"))]);

        var result = await validator.ValidateAsync("token");

        Assert.False(result.Succeeded);
        Assert.Contains("every", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Incomplete_trust_lane_disables_the_composite_fail_closed()
    {
        var validator = new CompositeTokenValidator(
            [new FakeValidator(TesseraTokenResult.Fail("off"), enabled: false), new FakeValidator(TesseraTokenResult.Fail("no"))]);

        Assert.False(validator.DelegationEnabled);
        var result = await validator.ValidateAsync("token");
        Assert.False(result.Succeeded);
        Assert.Contains("fail-closed", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Canonical_lane_preserves_subject_but_reuses_primary_owner_namespace()
    {
        var delegated = TesseraTokenResult.Success(new Dictionary<string, string>
        {
            ["sub"] = "stable-owner",
            ["iss"] = "https://auth.example/application/o/librechat/",
            ["email"] = "owner@example.com",
        });
        var validator = new CanonicalIssuerTokenValidator(
            new FakeValidator(delegated),
            "https://auth.example/application/o/tessera/");

        var result = await validator.ValidateAsync("token");

        Assert.True(result.Succeeded);
        Assert.Equal("stable-owner", result.Subject);
        Assert.Equal("https://auth.example/application/o/tessera/", result.Issuer);
    }

    private sealed class FakeValidator(TesseraTokenResult result, bool enabled = true) : ITokenValidator
    {
        public bool DelegationEnabled => enabled;
        public Task<TesseraTokenResult> ValidateAsync(string token, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }
}