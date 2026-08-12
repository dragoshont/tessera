using Tessera.Core.Kernel;
using Xunit;

namespace Tessera.Core.Tests.Kernel;

public sealed class PrincipalRefTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_builds_a_stable_identity_independent_of_display_hint()
    {
        var first = PrincipalRef.Create(
            "https://LOGIN.EXAMPLE.com/issuer",
            "tenant-a",
            "subject-42",
            "shared@example.com",
            CreatedAt);
        var renamed = PrincipalRef.Create(
            "https://login.example.com/issuer",
            "tenant-a",
            "subject-42",
            "New Display Name",
            CreatedAt.AddDays(1));

        Assert.Equal(first.PrincipalId, renamed.PrincipalId);
        Assert.Equal("https://login.example.com/issuer", first.Issuer);
        Assert.Equal(CreatedAt, first.CreatedAt);
    }

    [Fact]
    public void Create_keeps_same_display_hint_distinct_across_immutable_identity()
    {
        var first = PrincipalRef.Create(
            "https://issuer.example.com",
            "tenant-a",
            "subject-42",
            "shared@example.com",
            CreatedAt);
        var second = PrincipalRef.Create(
            "https://issuer.example.com",
            "tenant-b",
            "subject-42",
            "shared@example.com",
            CreatedAt);

        Assert.NotEqual(first.PrincipalId, second.PrincipalId);
    }

    [Theory]
    [InlineData("not-an-issuer", "tenant", "subject")]
    [InlineData("https://issuer.example.com", "", "subject")]
    [InlineData("https://issuer.example.com", "tenant", "subject\nother")]
    public void Create_rejects_noncanonical_identity_parts(string issuer, string tenant, string subject)
    {
        Assert.Throws<ArgumentException>(() =>
            PrincipalRef.Create(issuer, tenant, subject, null, CreatedAt));
    }
}