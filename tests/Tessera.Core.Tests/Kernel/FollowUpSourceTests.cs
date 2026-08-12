using Tessera.Core.Kernel;
using Xunit;

namespace Tessera.Core.Tests.Kernel;

public sealed class FollowUpSourceTests
{
    private const string Owner = "principal:owner";
    private readonly LocalFixtureSourceRecordAdapter _adapter = new();

    [Fact]
    public void Initial_fixture_extracts_candidates_without_current_state()
    {
        var source = Assert.IsType<SourceRecord>(_adapter.Read(Owner, "initial"));

        var extraction = DeterministicFollowUpExtractor.Extract(source);

        Assert.Equal(FollowUpExtractionStatus.Extracted, extraction.Status);
        Assert.Equal(3, extraction.Fields.Count);
        Assert.Contains(extraction.Fields, field => field.Field == FollowUpField.Deliverable
            && field.Value == "lease checklist");
        Assert.Null(extraction.Context);
    }

    [Theory]
    [InlineData("monday")]
    [InlineData("sent")]
    public void Incomplete_fixture_requires_exact_accepted_context(string fixtureId)
    {
        var source = Assert.IsType<SourceRecord>(_adapter.Read(Owner, fixtureId));

        var extraction = DeterministicFollowUpExtractor.Extract(source);

        Assert.Equal(FollowUpExtractionStatus.NeedsContext, extraction.Status);
        Assert.Empty(extraction.Fields);
    }

    [Fact]
    public void Corrected_current_context_drives_monday_and_completion()
    {
        var followUp = AcceptedFollowUp("lease renewal checklist");

        var monday = DeterministicFollowUpExtractor.Extract(
            Assert.IsType<SourceRecord>(_adapter.Read(Owner, "monday")),
            followUp);
        var sent = DeterministicFollowUpExtractor.Extract(
            Assert.IsType<SourceRecord>(_adapter.Read(Owner, "sent")),
            followUp);

        Assert.Equal("2026-08-17", Assert.Single(monday.Fields).Value);
        Assert.Equal(3, monday.Context!.CurrentFacts.Count);
        Assert.Contains(monday.Context.CurrentFacts, item => item.Content.Contains("lease renewal checklist", StringComparison.Ordinal));
        Assert.Equal(FollowUpField.CompletedAt, Assert.Single(sent.Fields).Field);
        Assert.Contains("deliverable-corrected", sent.Fields[0].ContextRevisionRefs);
        Assert.DoesNotContain("deliverable-original", sent.Fields[0].ContextRevisionRefs);
    }

    [Fact]
    public void Context_cannot_cross_owner_boundary()
    {
        var source = Assert.IsType<SourceRecord>(_adapter.Read("principal:other", "monday"));

        Assert.Throws<UnauthorizedAccessException>(() =>
            DeterministicFollowUpExtractor.Extract(source, AcceptedFollowUp("lease renewal checklist")));
    }

    private static FollowUp AcceptedFollowUp(string deliverable)
    {
        var timestamp = DateTimeOffset.Parse("2026-08-10T09:00:00Z");
        FollowUpRevision Revision(string id, FollowUpField field, string value) =>
            FollowUpRevision.Create(
                id,
                field,
                value,
                FollowUpRevisionState.Current,
                FollowUpFieldProvenance.Create(
                    [$"evidence-{id}"],
                    timestamp,
                    DeterministicFollowUpExtractor.ParserVersion,
                    0.99m),
                timestamp);

        return FollowUp.Create(
            "followup:r1-lease-rowan",
            Owner,
            FollowUpStatus.Tracked,
            [
                Revision("deliverable-original", FollowUpField.Deliverable, "lease checklist")
                    .WithState(FollowUpRevisionState.Superseded),
                Revision("deliverable-corrected", FollowUpField.Deliverable, deliverable),
                Revision("counterparty-current", FollowUpField.Counterparty, "Rowan"),
                Revision("due-current", FollowUpField.DueAt, "2026-08-14"),
            ],
            [],
            timestamp,
            timestamp,
            2);
    }
}