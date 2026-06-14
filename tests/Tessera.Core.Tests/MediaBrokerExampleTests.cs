using Tessera.Core.Configuration;
using Tessera.Core.Model;
using Tessera.Core.Policy;
using Tessera.Core.Resolution;
using Tessera.Core.Stores;
using Xunit;

namespace Tessera.Core.Tests;

/// <summary>
/// Guards the shipped media-broker example (ADR 0019/0020): the read/use/manage
/// planes are wired right, a member can't reach the control plane, step-up writes
/// step up, and user-delegated calls resolve the shared service key. Loading the
/// real file (not an inline copy) catches drift between the example and the engine.
/// </summary>
public sealed class MediaBrokerExampleTests
{
    private static LoadedPolicy LoadMediaExample()
    {
        var path = FindRepoFile(Path.Combine("deploy", "config", "grants.media.example.json"));
        return ConfigLoader.LoadPolicy(path);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new FileNotFoundException($"could not locate {relative} walking up from {AppContext.BaseDirectory}");
    }

    private static AccessRequest Req(string user, string target, string action) =>
        new(
            new Identity.CallerIdentity("chat://librechat", Identity.VerificationMethod.OidcJwt, "tessera.local"),
            target,
            action,
            new Identity.EndUserAssertion(user, "https://issuer.example/v2.0", Identity.VerificationMethod.OidcJwt, user));

    [Fact]
    public void Example_parses_with_service_owned_bindings_and_planed_tools()
    {
        var policy = LoadMediaExample();

        // All media keys are service-owned shared keys (nobody personally holds them).
        Assert.All(policy.Bindings, b => Assert.Equal(CredentialOwner.Service, b.Owner));
        Assert.All(policy.Bindings, b => Assert.Null(b.Principal));

        var seerr = policy.Recipes.Single(r => r.Target == "seerr");
        Assert.Equal(ActionPlane.Read, seerr.ExposedTools.Single(t => t.Name == "seerr_search").EffectivePlane);
        Assert.Equal(ActionPlane.Use, seerr.ExposedTools.Single(t => t.Name == "seerr_request").EffectivePlane);
        var approve = seerr.ExposedTools.Single(t => t.Name == "seerr_approve");
        Assert.Equal(ActionPlane.Use, approve.EffectivePlane);
        Assert.True(approve.StepUp);
        Assert.Equal(ActionPlane.Manage, seerr.ExposedTools.Single(t => t.Name == "seerr_settings").EffectivePlane);
    }

    [Fact]
    public void A_member_can_read_and_use_but_never_manage()
    {
        var pdp = new PolicyDecisionPoint(LoadMediaExample().Grants);

        // bob (member) may search + request …
        Assert.Equal(Effect.Allow, pdp.Evaluate(Req("bob@example.com", "seerr", "read:media")).Effect);
        Assert.Equal(Effect.Allow, pdp.Evaluate(Req("bob@example.com", "seerr", "use:request")).Effect);

        // … but not approve (operator) and not the control plane.
        Assert.Equal(Effect.Deny, pdp.Evaluate(Req("bob@example.com", "seerr", "use:approve")).Effect);
        Assert.Equal(Effect.Deny, pdp.Evaluate(Req("bob@example.com", "seerr", "manage:settings")).Effect);
    }

    [Fact]
    public void An_operator_reaches_manage_but_it_steps_up()
    {
        var pdp = new PolicyDecisionPoint(LoadMediaExample().Grants);

        // alice (operator) may manage, but the control plane defaults to step-up.
        Assert.Equal(Effect.StepUp, pdp.Evaluate(Req("alice@example.com", "seerr", "manage:settings")).Effect);
        // Her approve is step-up too (explicitly flagged); a plain use is allowed.
        Assert.Equal(Effect.StepUp, pdp.Evaluate(Req("alice@example.com", "seerr", "use:approve")).Effect);
        Assert.Equal(Effect.Allow, pdp.Evaluate(Req("alice@example.com", "seerr", "use:request")).Effect);
    }

    [Fact]
    public async Task A_member_call_resolves_the_shared_service_key()
    {
        var policy = LoadMediaExample();
        var store = new InMemoryCredentialStore();
        store.Put("seerr-api-key", new CredentialBundle(AccessToken: "shared"));
        var resolver = new CredentialResolver(policy.Bindings, store);

        // bob has no personal seerr binding → the shared service key backs his call.
        var result = await resolver.ResolveAsync(Req("bob@example.com", "seerr", "use:request"));
        Assert.Equal(CredentialStatus.Present, result.Status);
    }

    [Fact]
    public void Qbittorrent_delete_is_a_step_up_use_action()
    {
        var policy = LoadMediaExample();
        var qbt = policy.Recipes.Single(r => r.Target == "qbittorrent");
        var del = qbt.ExposedTools.Single(t => t.Name == "qbt_delete");
        Assert.Equal(ActionPlane.Use, del.EffectivePlane);
        Assert.True(del.StepUp);

        // An operator's delete steps up; a member has no use:delete grant at all.
        var pdp = new PolicyDecisionPoint(policy.Grants);
        Assert.Equal(Effect.StepUp, pdp.Evaluate(Req("alice@example.com", "qbittorrent", "use:delete")).Effect);
        Assert.Equal(Effect.Deny, pdp.Evaluate(Req("bob@example.com", "qbittorrent", "use:delete")).Effect);
    }
}
