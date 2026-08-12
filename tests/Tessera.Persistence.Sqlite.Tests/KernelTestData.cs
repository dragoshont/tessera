using System.Text;
using Tessera.Core.Kernel;

namespace Tessera.Persistence.Sqlite.Tests;

internal static class KernelTestData
{
    public static DateTimeOffset T0 { get; } = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

    public static ProducerRef Producer { get; } = ProducerRef.Create("sqlite-tests", "1");

    public static PrincipalRef Principal(
        string tenant = "tenant-a",
        string subject = "subject-1",
        string displayHint = "shared@example.com")
        => PrincipalRef.Create(
            "https://issuer.example.com",
            tenant,
            subject,
            displayHint,
            T0);

    public static EvidenceRecord Evidence(
        string ownerPrincipalId,
        string evidenceId,
        string excerpt,
        SensitivityClass sensitivity = SensitivityClass.Confidential)
        => EvidenceRecord.Create(
            evidenceId,
            ownerPrincipalId,
            "calendar",
            $"native-{evidenceId}",
            $"calendar://{evidenceId}",
            T0,
            T0,
            ActionPayloadHash.Algorithm,
            ActionPayloadHash.Version,
            ActionPayloadHash.Compute(Encoding.UTF8.GetBytes(excerpt)),
            RetentionState.Active,
            sensitivity,
            Producer,
            1,
            excerpt);

    public static ObservationEvent Event(
        string ownerPrincipalId,
        string eventId,
        string evidenceId,
        string eventType = "calendar.observed",
        DateTimeOffset? occurredAt = null)
        => ObservationEvent.Create(
            eventId,
            ownerPrincipalId,
            eventType,
            occurredAt ?? T0,
            occurredAt ?? T0,
            [ownerPrincipalId],
            ["appointment-1"],
            [evidenceId],
            new Dictionary<string, string> { ["source"] = "test" },
            Producer,
            1);

    public static AssertionRecord Assertion(
        string ownerPrincipalId,
        string assertionId,
        string value,
        AssertionType assertionType,
        EpistemicStatus status,
        string evidenceId,
        DateTimeOffset? createdAt = null)
        => AssertionRecord.Create(
            assertionId,
            ownerPrincipalId,
            "appointment-1",
            "start-time",
            value,
            assertionType,
            status,
            0.9m,
            createdAt ?? T0,
            null,
            createdAt ?? T0,
            null,
            [evidenceId],
            [],
            null,
            Producer,
            1);

    public static ActionRecord Action(
        string ownerPrincipalId,
        string actionId = "action-1",
        string capabilityVersion = "1",
        string payload = "{\"start\":\"16:30\"}",
        string idempotencyKey = "idempotency-1")
        => ActionRecord.Create(
            actionId,
            ownerPrincipalId,
            "calendar.update",
            capabilityVersion,
            "move appointment",
            ActionPayloadHash.Compute(Encoding.UTF8.GetBytes(payload)),
            "calendar/appointment-1",
            "external-write",
            "policy-decision-1",
            null,
            ActionState.Proposed,
            idempotencyKey,
            0,
            T0,
            null,
            null,
            null,
            null,
            null,
            1,
            0);

    public static WorkflowCheckpoint Workflow(string ownerPrincipalId, long version = 0)
        => WorkflowCheckpoint.Create(
            "workflow-1",
            ownerPrincipalId,
            "appointment-correction",
            version == 0 ? "WAITING_FOR_AUTHORIZATION" : "ACTION_STARTED",
            version == 0 ? "authorize" : "execute",
            ["evidence-1"],
            version == 0 ? [] : ["action-1"],
            null,
            T0,
            T0.AddMinutes(version),
            version);
}