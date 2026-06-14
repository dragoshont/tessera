using Tessera.Core.Results;
using Xunit;

namespace Tessera.Core.Tests;

public sealed class ResultEnvelopeTests
{
    [Fact]
    public void Metadata_envelope_carries_items_and_handles_but_no_body()
    {
        var items = new[]
        {
            new MetadataItem(new ResultHandle("gmail", "msg-1"), "Invoice", "billing@example.com"),
            new MetadataItem(new ResultHandle("gmail", "msg-2"), "Welcome", "team@example.com"),
        };
        var env = ResultEnvelope.Metadata("gmail", items);

        Assert.Equal(ResultClass.Metadata, env.Class);
        Assert.Null(env.Body);
        Assert.Null(env.Receipt);
        Assert.Equal(2, env.Items.Count);
        Assert.Equal("gmail:msg-1", env.Items[0].Handle.ToString());
    }

    [Fact]
    public void Full_body_and_preview_carry_a_body_and_no_items()
    {
        var full = ResultEnvelope.FullBody("gmail", "the full message text");
        Assert.Equal(ResultClass.FullBody, full.Class);
        Assert.Equal("the full message text", full.Body);
        Assert.Empty(full.Items);

        var preview = ResultEnvelope.Preview("gmail", "a short excerpt…");
        Assert.Equal(ResultClass.Preview, preview.Class);
        Assert.Empty(preview.Items);
    }

    [Fact]
    public void A_mutation_returns_a_receipt_never_a_body()
    {
        var env = ResultEnvelope.OfReceipt("graph-mail", new MutationReceipt("sent mail to bob@example.com", ObjectId: "AAMk", ConfirmationId: "conf-9"));

        Assert.Equal(ResultClass.Receipt, env.Class);
        Assert.Null(env.Body); // a write never returns freshly-fetched content
        Assert.Equal("AAMk", env.Receipt!.ObjectId);
        Assert.Equal("conf-9", env.Receipt.ConfirmationId);
    }

    [Fact]
    public void Handle_round_trips_through_its_wire_form()
    {
        var handle = new ResultHandle("gmail", "msg-42");
        var parsed = ResultHandle.Parse(handle.ToString(), "gmail");
        Assert.NotNull(parsed);
        Assert.Equal("gmail", parsed!.Target);
        Assert.Equal("msg-42", parsed.Value);
    }

    [Fact]
    public void A_handle_from_another_provider_is_rejected_no_cross_provider_replay()
    {
        // A handle minted for gmail must not be usable against graph-mail.
        Assert.Null(ResultHandle.Parse("gmail:msg-1", "graph-mail"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nocolon")]
    [InlineData("gmail:")]    // empty value
    [InlineData(":value")]    // empty target
    public void A_malformed_handle_is_rejected(string? raw) =>
        Assert.Null(ResultHandle.Parse(raw, "gmail"));

    [Fact]
    public void A_message_id_containing_a_colon_keeps_everything_after_the_first()
    {
        // Provider ids can contain colons; only the first separates target from value.
        var parsed = ResultHandle.Parse("gmail:a:b:c", "gmail");
        Assert.NotNull(parsed);
        Assert.Equal("a:b:c", parsed!.Value);
    }
}
