using Tessera.Core.Model;
using Tessera.Core.Policy;
using Xunit;

namespace Tessera.Core.Tests;

public sealed class PolicyTests
{
    private static Grant ChatbotReadsPortal => new(
        Caller: "spiffe://tessera.local/chatbot",
        Target: "health-portal",
        Actions: ["read:*"],
        OnBehalfOf: "alice@example.com");

    [Fact]
    public void Default_deny_when_no_grants()
    {
        var pdp = new PolicyDecisionPoint();
        var decision = pdp.Evaluate(TestData.Request(onBehalfOf: TestData.VerifiedUser()));

        Assert.False(decision.Allowed);
        Assert.Equal(Effect.Deny, decision.Effect);
        Assert.Contains("no grant", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Unverified_caller_is_denied_even_with_a_matching_grant()
    {
        var pdp = new PolicyDecisionPoint([ChatbotReadsPortal]);
        var request = new AccessRequest(
            TestData.UnverifiedCaller("spiffe://tessera.local/chatbot"),
            "health-portal",
            "read:results",
            TestData.VerifiedUser());

        var decision = pdp.Evaluate(request);

        Assert.False(decision.Allowed);
        Assert.Contains("not verified", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Unverified_enduser_is_denied()
    {
        var pdp = new PolicyDecisionPoint([ChatbotReadsPortal]);
        var request = TestData.Request(
            caller: TestData.VerifiedCaller("spiffe://tessera.local/chatbot"),
            onBehalfOf: TestData.UnverifiedUser("alice@example.com"));

        var decision = pdp.Evaluate(request);

        Assert.False(decision.Allowed);
        Assert.Contains("not verified", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Matching_grant_allows()
    {
        var pdp = new PolicyDecisionPoint([ChatbotReadsPortal]);
        var request = TestData.Request(
            caller: TestData.VerifiedCaller("spiffe://tessera.local/chatbot"),
            action: "read:results",
            onBehalfOf: TestData.VerifiedUser("alice@example.com"));

        var decision = pdp.Evaluate(request);

        Assert.True(decision.Allowed);
        Assert.Contains("alice@example.com", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Glob_actions_grant_reads_but_not_writes()
    {
        var pdp = new PolicyDecisionPoint([ChatbotReadsPortal]);
        var caller = TestData.VerifiedCaller("spiffe://tessera.local/chatbot");
        var user = TestData.VerifiedUser("alice@example.com");

        Assert.True(pdp.Evaluate(TestData.Request(caller, action: "read:anything", onBehalfOf: user)).Allowed);
        Assert.False(pdp.Evaluate(TestData.Request(caller, action: "write:results", onBehalfOf: user)).Allowed);
    }

    [Fact]
    public void Delegation_must_line_up_exactly()
    {
        var pdp = new PolicyDecisionPoint([ChatbotReadsPortal]);
        var caller = TestData.VerifiedCaller("spiffe://tessera.local/chatbot");

        // Right caller + target + action, but acting for the WRONG human → deny.
        var wrongUser = pdp.Evaluate(TestData.Request(caller, action: "read:results", onBehalfOf: TestData.VerifiedUser("bob@example.com")));
        Assert.False(wrongUser.Allowed);

        // A delegated grant never matches a no-human (automation) request.
        var noUser = pdp.Evaluate(TestData.Request(caller, action: "read:results"));
        Assert.False(noUser.Allowed);
    }

    [Fact]
    public void Automation_grant_matches_only_no_human_calls()
    {
        var crawler = new Grant(
            Caller: "spiffe://tessera.local/crawler",
            Target: "marketplace",
            Actions: ["read:listings"]);
        var pdp = new PolicyDecisionPoint([crawler]);
        var caller = TestData.VerifiedCaller("spiffe://tessera.local/crawler");

        Assert.True(pdp.Evaluate(new AccessRequest(caller, "marketplace", "read:listings")).Allowed);
        // The same caller "on behalf of" a human does NOT match the automation grant.
        Assert.False(pdp.Evaluate(new AccessRequest(caller, "marketplace", "read:listings", TestData.VerifiedUser())).Allowed);
    }

    [Fact]
    public void StepUp_actions_require_human_confirmation()
    {
        var payGrant = new Grant(
            Caller: "spiffe://tessera.local/n8n",
            Target: "marketplace",
            Actions: ["read:*", "write:order.pay"],
            OnBehalfOf: "alice@example.com",
            StepUpActions: ["write:*"]);
        var pdp = new PolicyDecisionPoint([payGrant]);
        var caller = TestData.VerifiedCaller("spiffe://tessera.local/n8n");
        var user = TestData.VerifiedUser("alice@example.com");

        Assert.Equal(Effect.Allow, pdp.Evaluate(new AccessRequest(caller, "marketplace", "read:orders", user)).Effect);
        var pay = pdp.Evaluate(new AccessRequest(caller, "marketplace", "write:order.pay", user));
        Assert.Equal(Effect.StepUp, pay.Effect);
        Assert.Equal("write:order.pay", pay.Obligations["step_up"]);
    }

    [Fact]
    public void AllowUnverified_only_relaxes_the_caller_gate_for_loopback_dev()
    {
        var grant = new Grant("dev-caller", "marketplace", ["read:*"]);
        var pdp = new PolicyDecisionPoint([grant], allowUnverified: true);
        var request = new AccessRequest(TestData.UnverifiedCaller("dev-caller"), "marketplace", "read:x");

        Assert.True(pdp.Evaluate(request).Allowed);
    }
}
