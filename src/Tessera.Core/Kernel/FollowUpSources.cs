namespace Tessera.Core.Kernel;

public sealed record SourceRecord
{
    private SourceRecord(
        string sourceRecordId,
        string ownerPrincipalId,
        string sourceType,
        string sourceNativeId,
        string sourceLocator,
        DateTimeOffset occurredAt,
        DateTimeOffset observedAt,
        string content,
        SensitivityClass sensitivity)
    {
        SourceRecordId = sourceRecordId;
        OwnerPrincipalId = ownerPrincipalId;
        SourceType = sourceType;
        SourceNativeId = sourceNativeId;
        SourceLocator = sourceLocator;
        OccurredAt = occurredAt;
        ObservedAt = observedAt;
        Content = content;
        Sensitivity = sensitivity;
    }

    public string SourceRecordId { get; }
    public string OwnerPrincipalId { get; }
    public string SourceType { get; }
    public string SourceNativeId { get; }
    public string SourceLocator { get; }
    public DateTimeOffset OccurredAt { get; }
    public DateTimeOffset ObservedAt { get; }
    public string Content { get; }
    public SensitivityClass Sensitivity { get; }

    public static SourceRecord Create(
        string sourceRecordId,
        string ownerPrincipalId,
        string sourceType,
        string sourceNativeId,
        string sourceLocator,
        DateTimeOffset occurredAt,
        DateTimeOffset observedAt,
        string content,
        SensitivityClass sensitivity)
        => new(
            KernelValidation.VisibleAscii(sourceRecordId, nameof(sourceRecordId), 128),
            KernelValidation.Text(ownerPrincipalId, nameof(ownerPrincipalId), 256),
            KernelValidation.VisibleAscii(sourceType, nameof(sourceType), 128),
            KernelValidation.VisibleAscii(sourceNativeId, nameof(sourceNativeId), 512),
            KernelValidation.PersistedNonSecretText(sourceLocator, nameof(sourceLocator), 2048)
                ?? throw new ArgumentException("Source locator is required.", nameof(sourceLocator)),
            KernelValidation.UtcTimestamp(occurredAt, nameof(occurredAt)),
            KernelValidation.UtcTimestamp(observedAt, nameof(observedAt)),
            KernelValidation.PersistedNonSecretText(content, nameof(content), 4096)
                ?? throw new ArgumentException("Source content is required.", nameof(content)),
            sensitivity);
}

public interface ISourceRecordAdapter
{
    SourceRecord? Read(string ownerPrincipalId, string sourceRecordId);
}

public sealed class UnavailableSourceRecordAdapter : ISourceRecordAdapter
{
    public SourceRecord? Read(string ownerPrincipalId,string sourceRecordId)=>null;
}

public sealed class LocalFixtureSourceRecordAdapter : ISourceRecordAdapter
{
    private static readonly Dictionary<string, Fixture> Fixtures =
        new Dictionary<string, Fixture>(StringComparer.Ordinal)
        {
            ["initial"] = new(
                "r1-initial",
                "I will send the lease checklist to Rowan by 2026-08-14.",
                DateTimeOffset.Parse("2026-08-10T09:00:00Z")),
            ["monday"] = new(
                "r1-monday",
                "Monday instead works for it.",
                DateTimeOffset.Parse("2026-08-11T09:00:00Z")),
            ["conflicting-friday"] = new(
                "r1-conflicting-friday",
                "The Friday 2026-08-14 deadline still stands.",
                DateTimeOffset.Parse("2026-08-18T09:00:00Z")),
            ["sent"] = new(
                "r1-sent",
                "Sent it to Rowan.",
                DateTimeOffset.Parse("2026-08-19T09:00:00Z")),
        };

    public SourceRecord? Read(string ownerPrincipalId, string sourceRecordId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRecordId);
        if (!Fixtures.TryGetValue(sourceRecordId, out var fixture))
        {
            return null;
        }

        return SourceRecord.Create(
            sourceRecordId,
            ownerPrincipalId,
            "local.fixture",
            fixture.NativeId,
            $"fixture://r1/{sourceRecordId}",
            fixture.OccurredAt,
            fixture.OccurredAt.AddMinutes(1),
            fixture.Content,
            SensitivityClass.Internal);
    }

    private sealed record Fixture(string NativeId, string Content, DateTimeOffset OccurredAt);
}

public enum FollowUpExtractionStatus
{
    Extracted,
    NeedsContext,
    Unsupported,
}

public sealed record ExtractedFollowUpField(
    FollowUpField Field,
    string Value,
    decimal Confidence,
    IReadOnlyList<string> ContextRevisionRefs,
    bool ConflictsWithCurrent = false);

public sealed record FollowUpExtraction(
    FollowUpExtractionStatus Status,
    IReadOnlyList<ExtractedFollowUpField> Fields,
    ContextEnvelope? Context = null);

public static class DeterministicFollowUpExtractor
{
    public const string ParserVersion = "followup.fixture.v1";

    public static FollowUpExtraction Extract(SourceRecord source, FollowUp? followUp = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (followUp is not null
            && !string.Equals(source.OwnerPrincipalId, followUp.OwnerPrincipalId, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Source and follow-up owners do not match.");
        }

        return source.Content switch
        {
            "I will send the lease checklist to Rowan by 2026-08-14." => Initial(),
            "Monday instead works for it." => Monday(source, followUp),
            "The Friday 2026-08-14 deadline still stands." => ConflictingFriday(followUp),
            "Sent it to Rowan." => Sent(source, followUp),
            _ => new FollowUpExtraction(FollowUpExtractionStatus.Unsupported, []),
        };
    }

    private static FollowUpExtraction Initial()
        => new(
            FollowUpExtractionStatus.Extracted,
            [
                new(FollowUpField.Deliverable, "lease checklist", 0.99m, []),
                new(FollowUpField.Counterparty, "Rowan", 0.99m, []),
                new(FollowUpField.DueAt, "2026-08-14", 0.99m, []),
            ]);

    private static FollowUpExtraction Monday(SourceRecord source, FollowUp? followUp)
    {
        var context = BuildContext(source, followUp, includeDueAt: true);
        if (context is null)
        {
            return new FollowUpExtraction(FollowUpExtractionStatus.NeedsContext, []);
        }

        return new FollowUpExtraction(
            FollowUpExtractionStatus.Extracted,
            [new ExtractedFollowUpField(
                FollowUpField.DueAt,
                "2026-08-17",
                0.95m,
                CurrentRevisionRefs(
                    followUp!,
                    FollowUpField.Deliverable,
                    FollowUpField.Counterparty,
                    FollowUpField.DueAt))],
            context);
    }

    private static FollowUpExtraction ConflictingFriday(FollowUp? followUp)
    {
        var dueAt = followUp?.CurrentField(FollowUpField.DueAt);
        if (dueAt is null)
        {
            return new FollowUpExtraction(FollowUpExtractionStatus.NeedsContext, []);
        }

        return new FollowUpExtraction(
            FollowUpExtractionStatus.Extracted,
            [new ExtractedFollowUpField(
                FollowUpField.DueAt,
                "2026-08-14",
                0.99m,
                [dueAt.RevisionId],
                ConflictsWithCurrent: true)]);
    }

    private static FollowUpExtraction Sent(SourceRecord source, FollowUp? followUp)
    {
        var context = BuildContext(source, followUp, includeDueAt: false);
        if (context is null)
        {
            return new FollowUpExtraction(FollowUpExtractionStatus.NeedsContext, []);
        }

        return new FollowUpExtraction(
            FollowUpExtractionStatus.Extracted,
            [new ExtractedFollowUpField(
                FollowUpField.CompletedAt,
                source.OccurredAt.ToUniversalTime().ToString("O"),
                0.95m,
                CurrentRevisionRefs(
                    followUp!,
                    FollowUpField.Deliverable,
                    FollowUpField.Counterparty))],
            context);
    }

    private static string[] CurrentRevisionRefs(
        FollowUp followUp,
        params FollowUpField[] fields)
        => fields
            .Select(field => followUp.CurrentField(field)!.RevisionId)
            .ToArray();

    private static ContextEnvelope? BuildContext(
        SourceRecord source,
        FollowUp? followUp,
        bool includeDueAt)
    {
        if (followUp is null || followUp.Status == FollowUpStatus.Conflict)
        {
            return null;
        }

        var required = includeDueAt
            ? new[] { FollowUpField.Deliverable, FollowUpField.Counterparty, FollowUpField.DueAt }
            : new[] { FollowUpField.Deliverable, FollowUpField.Counterparty };
        var revisions = required
            .Select(field => followUp.CurrentField(field))
            .ToArray();
        if (revisions.Any(revision => revision is null))
        {
            return null;
        }

        var items = revisions
            .Select(revision => ContextItem.Create(
                revision!.RevisionId,
                ContextItemKind.CurrentFact,
                $"follow-up {revision.Field} is {revision.Value}",
                SensitivityClass.Internal,
                1m,
                revision.Provenance.SourceTimestamp,
                revision.Provenance.EvidenceRefs))
            .ToArray();
        return ContextBuilder.Build(
            new ContextBuildRequest(
                source.OwnerPrincipalId,
                "interpret follow-up fixture",
                followUp.FollowUpId,
                2048,
                new HashSet<SensitivityClass> { SensitivityClass.Internal },
                []),
            items);
    }
}
