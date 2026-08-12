using System.Text.Json;
using Tessera.Core.Kernel;
using Xunit;

namespace Tessera.Persistence.Sqlite.Tests;

public sealed class KernelEndToEndTests
{
    [Fact]
    public async Task Fake_kernel_scenario_preserves_history_and_reconciles_after_restart()
    {
        using var database = new TemporaryDatabase();
        var principal = KernelTestData.Principal();
        var store = database.CreateStore();
        await store.InitializeAsync();
        await store.AddAsync(principal);

        var originalEvidence = KernelTestData.Evidence(
            principal.PrincipalId,
            "evidence-original",
            "Appointment is Tuesday at 15:00.",
            SensitivityClass.Internal);
        var originalEvent = KernelTestData.Event(
            principal.PrincipalId,
            "event-original",
            originalEvidence.EvidenceId);
        var originalCandidate = KernelTestData.Assertion(
            principal.PrincipalId,
            "assertion-original",
            "15:00",
            AssertionType.SourceAsserted,
            EpistemicStatus.Candidate,
            originalEvidence.EvidenceId);
        await store.AddObservationAsync(
            principal.PrincipalId,
            originalEvidence,
            originalEvent,
            originalCandidate);
        var originalCurrent = AssertionService.PromoteCandidate(originalCandidate, "trusted source rule");
        await store.SaveBatchAsync(principal.PrincipalId, [originalCurrent]);

        var correctedAt = KernelTestData.T0.AddHours(1);
        var correctionEvidence = KernelTestData.Evidence(
            principal.PrincipalId,
            "evidence-correction",
            "Actually it moved to 16:30.",
            SensitivityClass.Internal);
        var correctionEvent = KernelTestData.Event(
            principal.PrincipalId,
            "event-correction",
            correctionEvidence.EvidenceId,
            "calendar.corrected",
            correctedAt);
        var correctionCandidate = KernelTestData.Assertion(
            principal.PrincipalId,
            "assertion-correction",
            "16:30",
            AssertionType.UserAsserted,
            EpistemicStatus.Candidate,
            correctionEvidence.EvidenceId,
            correctedAt);
        await store.AddObservationAsync(
            principal.PrincipalId,
            correctionEvidence,
            correctionEvent,
            correctionCandidate);
        var corrected = AssertionService.Correct(
            originalCurrent,
            correctionCandidate,
            correctedAt,
            "explicit user correction");
        await store.ApplyCorrectionAsync(
            principal.PrincipalId,
            corrected.Superseded,
            corrected.Current);

        var restrictedEvidence = KernelTestData.Evidence(
            principal.PrincipalId,
            "evidence-restricted",
            "private provider detail",
            SensitivityClass.Restricted);
        await store.AddAsync(principal.PrincipalId, restrictedEvidence);
        var context = ContextBuilder.Build(
            new ContextBuildRequest(
                principal.PrincipalId,
                "move calendar appointment",
                "workflow-1",
                2048,
                new HashSet<SensitivityClass> { SensitivityClass.Internal },
                ["calendar.update"]),
            [
                ContextItem.Create(
                    corrected.Current.AssertionId,
                    ContextItemKind.CurrentFact,
                    $"appointment start is {corrected.Current.Value}",
                    SensitivityClass.Internal,
                    1m,
                    correctedAt,
                    corrected.Current.EvidenceRefs),
                ContextItem.Create(
                    restrictedEvidence.EvidenceId,
                    ContextItemKind.EvidenceReference,
                    restrictedEvidence.BoundedExcerpt!,
                    restrictedEvidence.Sensitivity,
                    0.8m,
                    restrictedEvidence.ObservedAt,
                    [restrictedEvidence.EvidenceId]),
            ]);
        Assert.Equal("appointment start is 16:30", Assert.Single(context.CurrentFacts).Content);
        Assert.Equal(
            ContextOmissionReason.SensitivityNotAllowed,
            Assert.Single(context.Omissions).Reason);

        var action = KernelTestData.Action(principal.PrincipalId);
        await store.AddAsync(principal.PrincipalId, action);
        var authorizationService = new ActionAuthorizationService(store);
        var authorization = await authorizationService.IssueAsync(
            action,
            correctedAt,
            correctedAt.AddMinutes(10));
        var authorized = await authorizationService.AuthorizeAsync(
            action,
            authorization.AuthorizationId,
            correctedAt.AddMinutes(1));
        Assert.NotNull(authorized);

        var invocationCount = 0;
        var fakeCapability = BuildUnknownOutcomeCapability(() => invocationCount++);
        var invocation = new CapabilityInvocation(
            principal.PrincipalId,
            "workflow-1",
            action.CapabilityId,
            action.CapabilityVersion,
            action.TargetScope,
            JsonDocument.Parse("{\"start\":\"16:30\"}").RootElement.Clone(),
            authorization.AuthorizationId,
            action.IdempotencyKey);
        var execution = new ActionExecutionService(store);
        var outcome = await execution.InvokeAsync(
            action.ActionId,
            authorized.Version,
            fakeCapability,
            invocation,
            correctedAt.AddMinutes(2));
        Assert.Equal(CapabilityOutcome.UnknownOutcome, outcome.Outcome);
        var durableStarted = await store.GetActionAsync(principal.PrincipalId, action.ActionId);
        Assert.NotNull(durableStarted);
        var reconciliation = durableStarted.TransitionTo(
            ActionState.ReconciliationRequired,
            correctedAt.AddMinutes(3),
            verificationState: "provider outcome unknown");
        Assert.True(await store.UpdateAsync(principal.PrincipalId, reconciliation, durableStarted.Version));

        var restarted = database.CreateStore();
        await restarted.InitializeAsync();
        var recovered = await restarted.GetActionAsync(principal.PrincipalId, action.ActionId);
        Assert.NotNull(recovered);
        Assert.Equal(ActionState.ReconciliationRequired, recovered.State);
        Assert.Equal(1, invocationCount);
        Assert.Equal(action.IdempotencyKey, recovered.IdempotencyKey);

        var verified = recovered.TransitionTo(
            ActionState.ProviderVerified,
            correctedAt.AddMinutes(4),
            providerReceipt: "fake-provider-state-matches",
            verificationState: "verified");
        Assert.True(await restarted.UpdateAsync(principal.PrincipalId, verified, recovered.Version));

        var timeline = await restarted.ListEventsAsync(principal.PrincipalId);
        var history = await restarted.ListHistoryAsync(
            principal.PrincipalId,
            corrected.Current.SubjectKey,
            corrected.Current.Predicate);
        var finalAction = await restarted.GetActionAsync(principal.PrincipalId, action.ActionId);
        Assert.Equal(2, timeline.Count);
        Assert.Contains(history, assertion => assertion.Value == "15:00"
            && assertion.EpistemicStatus == EpistemicStatus.Superseded);
        Assert.Contains(history, assertion => assertion.Value == "16:30"
            && assertion.EpistemicStatus == EpistemicStatus.Current);
        Assert.NotNull(finalAction);
        Assert.Equal(ActionState.ProviderVerified, finalAction.State);
    }

    private static DeterministicCapability BuildUnknownOutcomeCapability(Action onInvoke)
    {
        var descriptor = CapabilityDescriptor.Create(
            "calendar.update",
            "1",
            "Fake calendar update",
            "{}",
            "{}",
            SideEffectClass.ExternalReversible,
            ["calendar.write"],
            [SensitivityClass.Internal],
            IdempotencySupport.Keyed,
            VerificationSupport.ProviderState);
        return new DeterministicCapability(
            descriptor,
            _ =>
            {
                onInvoke();
                return new CapabilityResult(
                    CapabilityOutcome.UnknownOutcome,
                    JsonDocument.Parse("{}").RootElement.Clone(),
                    null,
                    "reconciliation required",
                    null);
            });
    }
}