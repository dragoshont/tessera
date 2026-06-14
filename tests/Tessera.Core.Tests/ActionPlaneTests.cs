using Tessera.Core.Policy;
using Xunit;

namespace Tessera.Core.Tests;

public sealed class ActionPlaneTests
{
    [Theory]
    [InlineData("read:appointments", ActionPlane.Read)]
    [InlineData("read:*", ActionPlane.Read)]
    [InlineData("use:book", ActionPlane.Use)]
    [InlineData("use:*", ActionPlane.Use)]
    [InlineData("manage:settings", ActionPlane.Manage)]
    [InlineData("manage:*", ActionPlane.Manage)]
    public void Namespaced_verbs_classify_by_prefix(string verb, ActionPlane expected) =>
        Assert.Equal(expected, ActionPlanes.Of(verb));

    [Theory]
    [InlineData("write:order")]   // legacy verb — not a recognised plane
    [InlineData("pay:invoice")]   // legacy verb
    [InlineData("appointments")]  // bare verb, no namespace
    [InlineData("*")]             // broad wildcard
    [InlineData("")]
    [InlineData(null)]
    public void Legacy_or_bare_verbs_are_unspecified(string? verb) =>
        Assert.Equal(ActionPlane.Unspecified, ActionPlanes.Of(verb));

    [Fact]
    public void Case_is_significant_so_a_capitalised_prefix_does_not_class_as_a_plane() =>
        // Verbs are lowercase by convention; the action Glob is case-sensitive too.
        Assert.Equal(ActionPlane.Unspecified, ActionPlanes.Of("Read:x"));

    [Theory]
    [InlineData("manage:settings", true)]
    [InlineData("manage:*", true)]
    [InlineData("use:*", false)]   // the data plane never counts as manage-scoped
    [InlineData("read:*", false)]
    [InlineData("*", false)]       // a broad wildcard is deliberately NOT manage-scoped
    public void IsManageScoped_only_for_a_manage_prefix(string pattern, bool expected) =>
        Assert.Equal(expected, ActionPlanes.IsManageScoped(pattern));

    [Fact]
    public void ToToken_round_trips_the_three_real_planes()
    {
        Assert.Equal("read", ActionPlanes.ToToken(ActionPlane.Read));
        Assert.Equal("use", ActionPlanes.ToToken(ActionPlane.Use));
        Assert.Equal("manage", ActionPlanes.ToToken(ActionPlane.Manage));
        Assert.Null(ActionPlanes.ToToken(ActionPlane.Unspecified));
    }

    [Fact]
    public void TokensOf_is_distinct_and_ordered_read_use_manage()
    {
        // Mixed order + duplicates + a legacy verb that contributes nothing.
        var tokens = ActionPlanes.TokensOf(["manage:x", "read:a", "use:b", "read:c", "write:legacy"]);
        Assert.Equal(["read", "use", "manage"], tokens);
    }

    [Fact]
    public void TokensOf_is_empty_when_no_verb_names_a_plane() =>
        Assert.Empty(ActionPlanes.TokensOf(["write:order", "pay:*", "bare"]));
}
