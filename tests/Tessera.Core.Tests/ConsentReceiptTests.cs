using Tessera.Core.Resolution;
using Tessera.Core.Results;
using Xunit;

namespace Tessera.Core.Tests;

public sealed class ConsentReceiptTests
{
    private static ConsentReceipt Calendar() => new(
        "alice@example.com", "graph-calendar", "calendar", CredentialOwner.User, DateTimeOffset.UtcNow,
        Scopes: ["Calendars.ReadBasic"]);

    [Fact]
    public void Covers_only_the_exact_principal_target_and_data_class()
    {
        var receipt = Calendar();
        Assert.True(receipt.Covers("alice@example.com", "graph-calendar", "calendar"));
        Assert.True(receipt.Covers("ALICE@example.com", "graph-calendar", "calendar")); // principal case-insensitive
    }

    [Fact]
    public void Calendar_consent_does_not_cover_mail()
    {
        // The whole point of per-data-class receipts: calendar != mail.
        var receipt = Calendar();
        Assert.False(receipt.Covers("alice@example.com", "graph-calendar", "mail.metadata"));
        Assert.False(receipt.Covers("alice@example.com", "graph-mail", "calendar"));
        Assert.False(receipt.Covers("bob@example.com", "graph-calendar", "calendar"));
    }

    [Fact]
    public void A_dependent_receipt_records_its_guardian()
    {
        var receipt = new ConsentReceipt(
            "kid@example.com", "graph-calendar", "calendar", CredentialOwner.Dependent, DateTimeOffset.UtcNow,
            Guardian: "alice@example.com");
        Assert.Equal(CredentialOwner.Dependent, receipt.Owner);
        Assert.Equal("alice@example.com", receipt.Guardian);
        Assert.Empty(receipt.CoveredScopes);
    }
}
