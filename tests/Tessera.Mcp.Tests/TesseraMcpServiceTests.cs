using Tessera.Core.Broker;
using Tessera.Core.Policy;
using Tessera.Core.Recipes;
using Tessera.Core.Resolution;
using Tessera.Core.Stores;
using Tessera.Identity;
using Xunit;

namespace Tessera.Mcp.Tests;

public sealed class TesseraMcpServiceTests
{
    private const string AliceToken = "alice-token";
    private const string CrawlerToken = "crawler-token";

    private static TesseraMcpService Build(ITokenValidator validator)
    {
        var store = new InMemoryCredentialStore();
        store.Put("health-portal-session", new CredentialBundle(AccessToken: "tok"));

        var grants = new[]
        {
            new Grant("chat://librechat", "health-portal", ["read:*"], "alice@example.com"),
            new Grant("app-crawler", "marketplace", ["read:listings"]),
        };
        var pdp = new PolicyDecisionPoint(grants);
        var resolver = new CredentialResolver(
            [new TargetBinding("health-portal", "health-portal-session", "alice@example.com")],
            store);
        var broker = new BrokerCore(pdp, resolver);
        var recipes = new List<Recipe>
        {
            new("health-portal", Actions: ["read:results"], Description: "patient portal"),
            new("marketplace", Actions: ["read:listings"]),
        };
        return new TesseraMcpService(validator, broker, pdp, recipes, new TesseraMcpOptions());
    }

    private static FakeTokenValidator Validator() =>
        new FakeTokenValidator()
            .AddUser(AliceToken, "oid-alice", "alice@example.com")
            .AddApp(CrawlerToken, "app-crawler");

    [Fact]
    public async Task WhoAmI_without_a_token_is_unauthenticated()
    {
        var result = await Build(Validator()).WhoAmIAsync(null);
        Assert.False(result.Authenticated);
        Assert.Contains("no forwarded", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhoAmI_with_a_user_token_reports_the_delegated_user()
    {
        var result = await Build(Validator()).WhoAmIAsync(AliceToken);
        Assert.True(result.Authenticated);
        Assert.Equal("chat://librechat", result.Caller);
        Assert.Equal("alice@example.com", result.User);
        Assert.False(result.IsAutomation);
    }

    [Fact]
    public async Task WhoAmI_with_an_app_token_reports_an_automation_caller()
    {
        var result = await Build(Validator()).WhoAmIAsync(CrawlerToken);
        Assert.True(result.Authenticated);
        Assert.Equal("app-crawler", result.Caller);
        Assert.True(result.IsAutomation);
        Assert.Null(result.User);
    }

    [Fact]
    public async Task CheckAccess_allows_a_granted_user_and_reports_present_credential()
    {
        var result = await Build(Validator()).CheckAccessAsync(AliceToken, "health-portal", "read:results");
        Assert.Equal("allow", result.Effect);
        Assert.Equal("present", result.CredentialStatus);
        Assert.True(result.Ok);
    }

    [Fact]
    public async Task CheckAccess_denies_an_ungranted_action()
    {
        var result = await Build(Validator()).CheckAccessAsync(AliceToken, "health-portal", "write:results");
        Assert.Equal("deny", result.Effect);
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task CheckAccess_without_a_token_denies_and_never_resolves()
    {
        var result = await Build(Validator()).CheckAccessAsync(null, "health-portal", "read:results");
        Assert.Equal("deny", result.Effect);
        Assert.Null(result.CredentialStatus);
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task CheckAccess_fails_closed_when_delegation_is_off()
    {
        var validator = new FakeTokenValidator { DelegationEnabled = false };
        var result = await Build(validator).CheckAccessAsync(AliceToken, "health-portal", "read:results");
        Assert.Equal("deny", result.Effect);
        Assert.Contains("fail-closed", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListTargets_marks_which_targets_are_granted()
    {
        var result = await Build(Validator()).ListTargetsAsync(AliceToken);
        Assert.True(result.Authenticated);

        var portal = Assert.Single(result.Targets, t => t.Target == "health-portal");
        Assert.True(portal.Granted);
        Assert.Equal("none", portal.Egress);

        var marketplace = Assert.Single(result.Targets, t => t.Target == "marketplace");
        Assert.False(marketplace.Granted); // alice's grant is for health-portal only
    }
}
