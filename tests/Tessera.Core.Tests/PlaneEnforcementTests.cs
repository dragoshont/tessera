using Tessera.Core.Identity;
using Tessera.Core.Model;
using Tessera.Core.Policy;
using Xunit;

namespace Tessera.Core.Tests;

/// <summary>
/// The control-plane (<c>manage:</c>) enforcement rules (ADR 0019): a broad grant
/// never silently reaches manage, and an authorized manage action defaults to a
/// human step-up. Read and use verbs keep their existing least-privilege behaviour.
/// </summary>
public sealed class PlaneEnforcementTests
{
    private static readonly EndUserAssertion Alice = TestData.VerifiedUser("alice@example.com");

    private static AccessRequest Manage(string action = "manage:settings") =>
        TestData.Request(
            caller: TestData.VerifiedCaller("spiffe://tessera.local/chat"),
            target: "utility-co",
            action: action,
            onBehalfOf: Alice);

    [Fact]
    public void A_broad_wildcard_grant_never_reaches_the_manage_plane()
    {
        // A grant of "*" authorizes read and use, but the control plane is default-deny.
        var grant = new Grant("spiffe://tessera.local/chat", "utility-co", ["*"], OnBehalfOf: "alice@example.com");
        var pdp = new PolicyDecisionPoint([grant]);

        Assert.Equal(Effect.Deny, pdp.Evaluate(Manage()).Effect);
        // … but the same broad grant still allows a use verb (no regression).
        Assert.NotEqual(Effect.Deny, pdp.Evaluate(Manage("use:pay")).Effect);
    }

    [Fact]
    public void A_use_star_grant_does_not_authorize_manage()
    {
        var grant = new Grant("spiffe://tessera.local/chat", "utility-co", ["read:*", "use:*"], OnBehalfOf: "alice@example.com");
        var pdp = new PolicyDecisionPoint([grant]);

        Assert.Equal(Effect.Deny, pdp.Evaluate(Manage()).Effect);
    }

    [Fact]
    public void A_manage_scoped_grant_authorizes_manage_but_defaults_to_step_up()
    {
        var grant = new Grant("spiffe://tessera.local/chat", "utility-co", ["manage:*"], OnBehalfOf: "alice@example.com");
        var pdp = new PolicyDecisionPoint([grant]); // manageRequiresStepUp defaults to true

        var decision = pdp.Evaluate(Manage());
        Assert.Equal(Effect.StepUp, decision.Effect);
        Assert.Equal("manage:settings", decision.Obligations["step_up"]);
    }

    [Fact]
    public void Manage_step_up_can_be_loosened_globally_for_an_operator_surface()
    {
        var grant = new Grant("spiffe://tessera.local/chat", "utility-co", ["manage:*"], OnBehalfOf: "alice@example.com");
        var pdp = new PolicyDecisionPoint([grant], manageRequiresStepUp: false);

        Assert.Equal(Effect.Allow, pdp.Evaluate(Manage()).Effect);
    }

    [Fact]
    public void An_explicit_step_up_action_still_wins_over_the_loosened_default()
    {
        // Even with the global manage step-up off, an explicitly flagged action steps up.
        var grant = new Grant(
            "spiffe://tessera.local/chat",
            "utility-co",
            ["manage:*"],
            OnBehalfOf: "alice@example.com",
            StepUpActions: ["manage:billing"]);
        var pdp = new PolicyDecisionPoint([grant], manageRequiresStepUp: false);

        Assert.Equal(Effect.StepUp, pdp.Evaluate(Manage("manage:billing")).Effect);
        Assert.Equal(Effect.Allow, pdp.Evaluate(Manage("manage:theme")).Effect);
    }

    [Fact]
    public void Manage_default_step_up_does_not_disturb_read_or_use()
    {
        var grant = new Grant("spiffe://tessera.local/chat", "utility-co", ["read:*", "use:pay"], OnBehalfOf: "alice@example.com");
        var pdp = new PolicyDecisionPoint([grant]);

        Assert.Equal(Effect.Allow, pdp.Evaluate(Manage("read:bill")).Effect);
        Assert.Equal(Effect.Allow, pdp.Evaluate(Manage("use:pay")).Effect);
    }
}
