using Tessera.Core.Portal;
using Tessera.Core.Resolution;
using Xunit;

namespace Tessera.Core.Tests;

public sealed class GuardianRelationshipsTests
{
    private static GuardianRelationships Build() => new(
    [
        // alice (guardian) seeded a dependent binding for kid.
        new TargetBinding("health-portal", "hp-kid", "kid@example.com", CredentialOwner.Dependent, "alice@example.com"),
        new TargetBinding("graph-calendar", "cal-kid", "kid@example.com", CredentialOwner.Dependent, "alice@example.com"),
        // alice's own user binding (not a guardianship).
        new TargetBinding("health-portal", "hp-alice", "alice@example.com", CredentialOwner.User),
        // a shared service key (no guardianship).
        new TargetBinding("seerr", "seerr-key", Principal: null, Owner: CredentialOwner.Service),
    ]);

    [Fact]
    public void A_guardian_may_act_as_the_dependent_they_seeded()
    {
        var rels = Build();
        Assert.True(rels.MayActAs("alice@example.com", "kid@example.com"));
        Assert.True(rels.MayActAs("ALICE@example.com", "KID@example.com")); // case-insensitive
    }

    [Fact]
    public void A_non_guardian_may_not_act_as_the_dependent()
    {
        var rels = Build();
        Assert.False(rels.MayActAs("bob@example.com", "kid@example.com"));
        // The dependent can't act as themselves via this relationship, and nobody
        // can act as alice (she has no guardian).
        Assert.False(rels.MayActAs("kid@example.com", "kid@example.com"));
        Assert.False(rels.MayActAs("bob@example.com", "alice@example.com"));
    }

    [Fact]
    public void A_user_or_service_binding_creates_no_guardianship()
    {
        // Only owner: dependent bindings establish a relationship.
        var rels = new GuardianRelationships(
        [
            new TargetBinding("health-portal", "hp-alice", "alice@example.com", CredentialOwner.User, "someone@example.com"),
        ]);
        Assert.False(rels.MayActAs("someone@example.com", "alice@example.com"));
    }

    [Fact]
    public void DependentsOf_lists_the_distinct_dependents_a_guardian_seeded()
    {
        var rels = Build();
        Assert.Equal(["kid@example.com"], rels.DependentsOf("alice@example.com"));
        Assert.Empty(rels.DependentsOf("bob@example.com"));
    }
}
