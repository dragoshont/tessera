using Tessera.Core.Configuration;
using Tessera.Core.Policy;
using Tessera.Core.Portal;
using Tessera.Core.Resolution;
using Xunit;

namespace Tessera.Core.Tests;

/// <summary>
/// The portal "people" projection (ADR 0016 / admin-portal spec §7b): people are
/// derived from the policy + an admins allow-list, with <b>no database</b>. These
/// tests use generic identities — <c>alice</c> is the operator (the live admin
/// account), <c>bob</c> and <c>carol</c> are the two members — exactly the shape
/// the maintainer asked for ("my account is admin; I can see the other two as
/// users").
/// </summary>
public sealed class PortalPeopleTests
{
    private const string Admin = "alice@example.com";
    private const string Member1 = "bob@example.com";
    private const string Member2 = "carol@example.com";

    private static LoadedPolicy PolicyFor(params string[] principals)
    {
        // One grant + one binding per principal, as the connect flow would author.
        var grants = principals
            .Select(p => new Grant(
                Caller: "chat://librechat",
                Target: "health-portal",
                Actions: ["read:*"],
                OnBehalfOf: p))
            .ToArray();

        var bindings = principals
            .Select(p => new TargetBinding("health-portal", $"health-portal-{p}", p))
            .ToArray();

        return new LoadedPolicy(grants, bindings, []);
    }

    [Fact]
    public void Admin_in_allow_list_is_admin_others_are_members()
    {
        var policy = PolicyFor(Admin, Member1, Member2);

        var people = PortalPeople.Project(policy, admins: [Admin]);

        // All three appear …
        Assert.Equal(3, people.Count);
        Assert.Contains(people, p => p.Principal == Admin && p.Role == PortalRole.Admin);
        Assert.Contains(people, p => p.Principal == Member1 && p.Role == PortalRole.Member);
        Assert.Contains(people, p => p.Principal == Member2 && p.Role == PortalRole.Member);
        // … and exactly one is the operator.
        Assert.Single(people, p => p.Role == PortalRole.Admin);
    }

    [Fact]
    public void Admin_is_listed_first_then_members_alphabetically()
    {
        // Author them out of order to prove ordering is by role then name, not file order.
        var policy = PolicyFor(Member2, Member1, Admin);

        var people = PortalPeople.Project(policy, admins: [Admin]);

        Assert.Equal(Admin, people[0].Principal);   // operator on top
        Assert.Equal(Member1, people[1].Principal);  // bob before carol
        Assert.Equal(Member2, people[2].Principal);
    }

    [Fact]
    public void Connection_count_reflects_bindings_per_person()
    {
        // alice has two connections, bob has one.
        var policy = new LoadedPolicy(
            Grants: [],
            Bindings:
            [
                new TargetBinding("health-portal", "health-portal-alice", Admin),
                new TargetBinding("utility-co", "utility-co-alice", Admin),
                new TargetBinding("health-portal", "health-portal-bob", Member1),
            ],
            Recipes: []);

        var people = PortalPeople.Project(policy, admins: [Admin]);

        Assert.Equal(2, people.Single(p => p.Principal == Admin).ConnectionCount);
        Assert.Equal(1, people.Single(p => p.Principal == Member1).ConnectionCount);
    }

    [Fact]
    public void Admin_with_no_connections_still_appears()
    {
        // The operator is a person even before connecting anything.
        var people = PortalPeople.Project(LoadedPolicy.Empty, admins: [Admin]);

        var operatorPerson = Assert.Single(people);
        Assert.Equal(Admin, operatorPerson.Principal);
        Assert.Equal(PortalRole.Admin, operatorPerson.Role);
        Assert.Equal(0, operatorPerson.ConnectionCount);
    }

    [Fact]
    public void Automation_bindings_without_a_principal_are_not_people()
    {
        var policy = new LoadedPolicy(
            Grants: [],
            Bindings: [new TargetBinding("internal-api", "internal-api-key", Principal: null)],
            Recipes: []);

        Assert.Empty(PortalPeople.Project(policy, admins: []));
    }

    [Fact]
    public void People_are_deduplicated_case_insensitively()
    {
        // Same person spelled two ways across a grant and a binding → one person.
        var policy = new LoadedPolicy(
            Grants: [new Grant("chat://librechat", "health-portal", ["read:*"], OnBehalfOf: "Bob@Example.com")],
            Bindings: [new TargetBinding("health-portal", "health-portal-bob", "bob@example.com")],
            Recipes: []);

        var person = Assert.Single(PortalPeople.Project(policy, admins: []));
        Assert.Equal(1, person.ConnectionCount);
        Assert.Equal(PortalRole.Member, person.Role);
    }

    [Fact]
    public void Admin_match_is_case_insensitive()
    {
        var policy = PolicyFor(Member1);

        var people = PortalPeople.Project(policy, admins: ["BOB@EXAMPLE.COM"]);

        Assert.Equal(PortalRole.Admin, Assert.Single(people).Role);
    }

    [Fact]
    public void Empty_policy_and_no_admins_yields_no_people()
    {
        Assert.Empty(PortalPeople.Project(LoadedPolicy.Empty, admins: []));
    }

    [Fact]
    public void Default_config_has_no_portal_admins()
    {
        // Fail-safe: the portal ships with no operator until one is named in config.
        Assert.Empty(new TesseraConfig().Portal.Admins);
        Assert.Empty(new TesseraConfig().Validate());
    }
}
