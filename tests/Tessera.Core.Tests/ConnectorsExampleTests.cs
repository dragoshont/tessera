using Tessera.Core.Configuration;
using Tessera.Core.Identity;
using Tessera.Core.Model;
using Tessera.Core.Policy;
using Tessera.Core.Recipes;
using Tessera.Core.Resolution;
using Xunit;

namespace Tessera.Core.Tests;

/// <summary>
/// Guards the shipped personal-data connectors example (ADR 0019/0020, Gmail/Graph):
/// all bindings are owner: user (the person's own login), mail is metadata-first,
/// and sends are step-up. Loads the real file so the example can't drift from the
/// engine.
/// </summary>
public sealed class ConnectorsExampleTests
{
    private static LoadedPolicy Load()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "deploy", "config", "grants.connectors.example.json");
            if (File.Exists(candidate))
            {
                return ConfigLoader.LoadPolicy(candidate);
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new FileNotFoundException("grants.connectors.example.json");
    }

    private static AccessRequest Req(string target, string action) =>
        new(
            new CallerIdentity("chat://librechat", VerificationMethod.OidcJwt, "tessera.local"),
            target,
            action,
            new EndUserAssertion("alice@example.com", "https://issuer.example/v2.0", VerificationMethod.OidcJwt, "alice@example.com"));

    [Fact]
    public void Every_connector_binding_is_user_owned()
    {
        // Personal data: Tessera holds the person's OWN login, never a shared key.
        Assert.All(Load().Bindings, b => Assert.Equal(CredentialOwner.User, b.Owner));
    }

    [Fact]
    public void Mail_is_metadata_first_with_a_body_by_handle_and_a_step_up_send()
    {
        var policy = Load();
        var gmail = policy.Recipes.Single(r => r.Target == "gmail");

        Assert.Equal(ActionPlane.Read, gmail.ExposedTools.Single(t => t.Name == "gmail_search").EffectivePlane);
        Assert.Equal("read:mail.metadata", gmail.ExposedTools.Single(t => t.Name == "gmail_search").Action);
        Assert.Equal("read:mail.body", gmail.ExposedTools.Single(t => t.Name == "gmail_read").Action);

        var send = gmail.ExposedTools.Single(t => t.Name == "gmail_send");
        Assert.Equal(ActionPlane.Use, send.EffectivePlane);
        Assert.True(send.StepUp);
    }

    [Fact]
    public void Sending_mail_steps_up_reading_does_not()
    {
        var pdp = new PolicyDecisionPoint(Load().Grants);

        Assert.Equal(Effect.Allow, pdp.Evaluate(Req("gmail", "read:mail.metadata")).Effect);
        Assert.Equal(Effect.Allow, pdp.Evaluate(Req("gmail", "read:mail.body")).Effect);
        Assert.Equal(Effect.StepUp, pdp.Evaluate(Req("gmail", "use:mail.send")).Effect);
    }

    [Fact]
    public void Calendar_consent_does_not_grant_mail()
    {
        // Separate consent per data class: the calendar grant must not reach mail.
        var pdp = new PolicyDecisionPoint(Load().Grants);

        Assert.Equal(Effect.Allow, pdp.Evaluate(Req("graph-calendar", "read:calendar")).Effect);
        // No mail action is authorized on the calendar target.
        Assert.Equal(Effect.Deny, pdp.Evaluate(Req("graph-calendar", "read:mail.metadata")).Effect);
    }

    [Fact]
    public void Graph_calendar_declares_tessera_owned_refresh()
    {
        var cal = Load().Recipes.Single(r => r.Target == "graph-calendar");
        Assert.Equal("tessera", cal.Rotation!.Owner);
        Assert.NotNull(cal.Refresh);
    }
}
