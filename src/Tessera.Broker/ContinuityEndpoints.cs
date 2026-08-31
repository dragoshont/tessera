using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Tessera.Core.Configuration;
using Tessera.Core.Identity;
using Tessera.Core.Kernel;
using Tessera.Identity;

namespace Tessera.Broker;

internal static class ContinuityEndpoints
{
    public static void MapContinuityEndpoints(this WebApplication app)
    {
        app.MapGet("/portal/continuity/follow-ups", async (
            HttpContext context,
            ITokenValidator validator,
            TesseraConfig config,
            IServiceProvider services,
            string? view,
            CancellationToken cancellationToken) =>
        {
            var boundary = await ResolveBoundaryAsync(
                context,
                validator,
                config,
                services,
                cancellationToken).ConfigureAwait(false);
            if (boundary.Error is not null)
            {
                return boundary.Error;
            }

            var filtered = view?.Trim().ToLowerInvariant() switch
            {
                null or "" => await boundary.Service!.ListAsync(boundary.OwnerPrincipalId!, limit: 101, cancellationToken: cancellationToken).ConfigureAwait(false),
                "attention" => await ListViewsAsync(boundary.Service!, boundary.OwnerPrincipalId!, FollowUpStatus.Attention, FollowUpStatus.Conflict, cancellationToken).ConfigureAwait(false),
                "tracked" => await ListViewsAsync(boundary.Service!, boundary.OwnerPrincipalId!, FollowUpStatus.Tracked, FollowUpStatus.Completed, cancellationToken).ConfigureAwait(false),
                _ => null,
            };
            if (filtered is null)
            {
                return Error(400, "invalid_view", "View must be attention or tracked.");
            }

            return Results.Json(new FollowUpListDto(
                filtered.Take(100).Select(ToSummaryDto).ToArray(),
                filtered.Count > 100));
        });

        app.MapGet("/portal/continuity/follow-ups/{followUpId}", async (
            HttpContext context,
            string followUpId,
            ITokenValidator validator,
            TesseraConfig config,
            IServiceProvider services,
            CancellationToken cancellationToken) =>
        {
            var boundary = await ResolveBoundaryAsync(
                context,
                validator,
                config,
                services,
                cancellationToken).ConfigureAwait(false);
            if (boundary.Error is not null)
            {
                return boundary.Error;
            }

            var followUp = await boundary.Service!.GetAsync(
                boundary.OwnerPrincipalId!,
                followUpId,
                cancellationToken).ConfigureAwait(false);
            return followUp is null
                ? Error(404, "not_found", "FollowUp not found.")
                : Results.Json(ToDetailDto(followUp));
        });

        app.MapGet("/portal/continuity/follow-ups/{followUpId}/why", async (
            HttpContext context,
            string followUpId,
            ITokenValidator validator,
            TesseraConfig config,
            IServiceProvider services,
            CancellationToken cancellationToken) =>
        {
            var boundary = await ResolveBoundaryAsync(
                context,
                validator,
                config,
                services,
                cancellationToken).ConfigureAwait(false);
            if (boundary.Error is not null)
            {
                return boundary.Error;
            }

            var followUp = await boundary.Service!.GetAsync(
                boundary.OwnerPrincipalId!,
                followUpId,
                cancellationToken).ConfigureAwait(false);
            return followUp is null
                ? Error(404, "not_found", "FollowUp not found.")
                : Results.Json(ToWhyDto(followUp));
        });

        app.MapPost("/portal/continuity/fixtures/{fixtureId}/import", async (
            HttpContext context,
            string fixtureId,
            ImportFixtureRequest? request,
            ITokenValidator validator,
            TesseraConfig config,
            IServiceProvider services,
            CancellationToken cancellationToken) =>
            await ExecuteMutationAsync(
                context,
                validator,
                config,
                services,
                request is null
                    ? null
                    : boundary => boundary.Service!.ImportFixtureAsync(
                        boundary.OwnerPrincipalId!,
                        fixtureId,
                        request.OperationId,
                        request.FollowUpId,
                        request.ExpectedVersion,
                        cancellationToken),
                cancellationToken).ConfigureAwait(false));

        app.MapPost("/portal/continuity/follow-ups/{followUpId}/accept", async (
            HttpContext context,
            string followUpId,
            AcceptFollowUpRequest? request,
            ITokenValidator validator,
            TesseraConfig config,
            IServiceProvider services,
            CancellationToken cancellationToken) =>
            await ExecuteMutationAsync(
                context,
                validator,
                config,
                services,
                request is null
                    ? null
                    : boundary => boundary.Service!.AcceptAsync(
                        boundary.OwnerPrincipalId!,
                        followUpId,
                        request.OperationId,
                        request.ExpectedVersion,
                        request.CandidateRevisionIds,
                        cancellationToken),
                cancellationToken).ConfigureAwait(false));

        app.MapPost("/portal/continuity/follow-ups/{followUpId}/correct", async (
            HttpContext context,
            string followUpId,
            FieldDecisionRequest? request,
            ITokenValidator validator,
            TesseraConfig config,
            IServiceProvider services,
            CancellationToken cancellationToken) =>
            await ExecuteFieldMutationAsync(
                context,
                followUpId,
                request,
                validator,
                config,
                services,
                static (service, owner, id, operation, version, field, value, token) =>
                    service.CorrectAsync(owner, id, operation, version, field, value, token),
                cancellationToken).ConfigureAwait(false));

        app.MapPost("/portal/continuity/follow-ups/{followUpId}/resolve", async (
            HttpContext context,
            string followUpId,
            FieldDecisionRequest? request,
            ITokenValidator validator,
            TesseraConfig config,
            IServiceProvider services,
            CancellationToken cancellationToken) =>
            await ExecuteFieldMutationAsync(
                context,
                followUpId,
                request,
                validator,
                config,
                services,
                static (service, owner, id, operation, version, field, value, token) =>
                    service.ResolveAsync(owner, id, operation, version, field, value, token),
                cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IReadOnlyList<FollowUp>> ListViewsAsync(
        FollowUpContinuityService service,
        string ownerPrincipalId,
        FollowUpStatus first,
        FollowUpStatus second,
        CancellationToken cancellationToken)
    {
        var firstItems = await service.ListAsync(ownerPrincipalId, first, 101, cancellationToken).ConfigureAwait(false);
        var secondItems = await service.ListAsync(ownerPrincipalId, second, 101, cancellationToken).ConfigureAwait(false);
        return firstItems.Concat(secondItems)
            .OrderByDescending(item => item.UpdatedAt)
            .ThenBy(item => item.FollowUpId, StringComparer.Ordinal)
            .Take(101)
            .ToArray();
    }

    private static async Task<IResult> ExecuteFieldMutationAsync(
        HttpContext context,
        string followUpId,
        FieldDecisionRequest? request,
        ITokenValidator validator,
        TesseraConfig config,
        IServiceProvider services,
        Func<FollowUpContinuityService, string, string, string, long, FollowUpField, string, CancellationToken, Task<FollowUpCommitResult>> mutation,
        CancellationToken cancellationToken)
    {
        if (request is null || !TryParseField(request.Field, out var field))
        {
            return Error(400, "invalid_request", "A supported field is required.");
        }

        return await ExecuteMutationAsync(
            context,
            validator,
            config,
            services,
            boundary => mutation(
                boundary.Service!,
                boundary.OwnerPrincipalId!,
                followUpId,
                request.OperationId,
                request.ExpectedVersion,
                field,
                request.Value,
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> ExecuteMutationAsync(
        HttpContext context,
        ITokenValidator validator,
        TesseraConfig config,
        IServiceProvider services,
        Func<ContinuityBoundary, Task<FollowUpCommitResult>>? mutation,
        CancellationToken cancellationToken)
    {
        if (mutation is null)
        {
            return Error(400, "invalid_request", "A request body is required.");
        }

        var boundary = await ResolveBoundaryAsync(
            context,
            validator,
            config,
            services,
            cancellationToken).ConfigureAwait(false);
        if (boundary.Error is not null)
        {
            return boundary.Error;
        }

        try
        {
            var result = await mutation(boundary).ConfigureAwait(false);
            return Results.Json(new FollowUpMutationDto(
                result.FollowUp.FollowUpId,
                result.ResultVersion,
                result.Replayed));
        }
        catch (FollowUpNeedsContextException exception)
        {
            return Error(422, "needs_context", exception.Message);
        }
        catch (KeyNotFoundException exception)
        {
            return Error(404, "not_found", exception.Message);
        }
        catch (FollowUpConcurrencyException exception)
        {
            return Error(409, "stale_version", exception.Message);
        }
        catch (FollowUpOperationConflictException exception)
        {
            return Error(409, "operation_conflict", exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Error(409, "invalid_state", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return Error(400, "invalid_request", exception.Message);
        }
        catch (NotSupportedException exception)
        {
            return Error(400, "unsupported", exception.Message);
        }
    }

    private static async Task<ContinuityBoundary> ResolveBoundaryAsync(
        HttpContext context,
        ITokenValidator validator,
        TesseraConfig config,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var user = await PortalEndpoints.ResolveEndUserAsync(context, validator, config).ConfigureAwait(false);
        if (user?.CanonicalPrincipalId is null || string.IsNullOrWhiteSpace(user.TenantId))
        {
            return new ContinuityBoundary(Error: Error(
                401,
                "unauthenticated",
                "Canonical issuer, tenant, and subject are required."));
        }

        var service = services.GetService<FollowUpContinuityService>();
        var principals = services.GetService<IPrincipalRepository>();
        if (service is null || principals is null)
        {
            return new ContinuityBoundary(Error: Error(
                503,
                "continuity_unavailable",
                "Local continuity storage is not configured."));
        }

        var principal = PrincipalRef.Create(
            user.Issuer,
            user.TenantId,
            user.Subject,
            user.PreferredUsername,
            DateTimeOffset.UtcNow);
        await PrincipalRegistration.RegisterForMutationAsync(
                context,
                principals,
                principal,
                cancellationToken)
            .ConfigureAwait(false);
        return new ContinuityBoundary(service, principal.PrincipalId);
    }

    private static FollowUpSummaryDto ToSummaryDto(FollowUp followUp)
        => new(
            followUp.FollowUpId,
            StatusToken(followUp.Status),
            followUp.Version,
            followUp.CurrentField(FollowUpField.Deliverable)?.Value,
            followUp.CurrentField(FollowUpField.Counterparty)?.Value,
            followUp.CurrentField(FollowUpField.DueAt)?.Value,
            followUp.Candidates.Count,
            followUp.Revisions.Count(revision => revision.State == FollowUpRevisionState.Conflicted),
            followUp.UpdatedAt);

    private static FollowUpDetailDto ToDetailDto(FollowUp followUp)
    {
        var timeline = followUp.Timeline.TakeLast(100).Select(ToTimelineDto).ToArray();
        return new FollowUpDetailDto(
            followUp.FollowUpId,
            StatusToken(followUp.Status),
            followUp.Version,
            followUp.CreatedAt,
            followUp.UpdatedAt,
            followUp.Revisions.Select(ToRevisionDto).ToArray(),
            timeline,
            followUp.Timeline.Count > timeline.Length);
    }

    private static FollowUpWhyDto ToWhyDto(FollowUp followUp)
    {
        var ordered = followUp.Revisions
            .OrderByDescending(revision => revision.CreatedAt)
            .ThenBy(revision => revision.RevisionId, StringComparer.Ordinal)
            .ToArray();
        var bounded = ordered.Take(100).ToArray();
        return new FollowUpWhyDto(
            followUp.FollowUpId,
            bounded
                .GroupBy(revision => FieldToken(revision.Field), StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(ToRevisionDto).ToArray(),
                    StringComparer.Ordinal),
            ordered.Length > bounded.Length);
    }

    private static FollowUpRevisionDto ToRevisionDto(FollowUpRevision revision)
        => new(
            revision.RevisionId,
            FieldToken(revision.Field),
            revision.Value,
            revision.State.ToString().ToLowerInvariant(),
            revision.Provenance.EvidenceRefs,
            revision.Provenance.SourceTimestamp,
            revision.Provenance.ParserVersion,
            revision.Provenance.Confidence,
            revision.Provenance.CorrectionEvidenceRef,
            revision.Provenance.LineageRevisionRefs,
            revision.CreatedAt);

    private static FollowUpTimelineDto ToTimelineDto(FollowUpTimelineEntry entry)
        => new(
            entry.Sequence,
            entry.Kind.ToString(),
            entry.Field is null ? null : FieldToken(entry.Field.Value),
            entry.Summary,
            entry.EvidenceRef,
            entry.SourceTimestamp,
            entry.RecordedAt);

    private static string StatusToken(FollowUpStatus status) => status.ToString().ToLowerInvariant();

    private static string FieldToken(FollowUpField field) => field switch
    {
        FollowUpField.Deliverable => "deliverable",
        FollowUpField.Counterparty => "counterparty",
        FollowUpField.DueAt => "dueAt",
        FollowUpField.CompletedAt => "completedAt",
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    private static bool TryParseField(string? value, out FollowUpField field)
    {
        field = value switch
        {
            "deliverable" => FollowUpField.Deliverable,
            "counterparty" => FollowUpField.Counterparty,
            "dueAt" => FollowUpField.DueAt,
            "completedAt" => FollowUpField.CompletedAt,
            _ => default,
        };
        return value is "deliverable" or "counterparty" or "dueAt" or "completedAt";
    }

    private static IResult Error(int statusCode, string code, string message)
        => Results.Json(new ContinuityErrorDto(code, message), statusCode: statusCode);

    private sealed record ContinuityBoundary(
        FollowUpContinuityService? Service = null,
        string? OwnerPrincipalId = null,
        IResult? Error = null);
}

internal sealed record ImportFixtureRequest(
    string OperationId,
    string? FollowUpId,
    long? ExpectedVersion);

internal sealed record AcceptFollowUpRequest(
    string OperationId,
    long ExpectedVersion,
    IReadOnlyList<string>? CandidateRevisionIds);

internal sealed record FieldDecisionRequest(
    string OperationId,
    long ExpectedVersion,
    string Field,
    string Value);

internal sealed record FollowUpMutationDto(string FollowUpId, long Version, bool Replayed);

internal sealed record FollowUpListDto(IReadOnlyList<FollowUpSummaryDto> Items, bool Truncated);

internal sealed record FollowUpSummaryDto(
    string FollowUpId,
    string Status,
    long Version,
    string? Deliverable,
    string? Counterparty,
    string? DueAt,
    int CandidateCount,
    int ConflictCount,
    DateTimeOffset UpdatedAt);

internal sealed record FollowUpDetailDto(
    string FollowUpId,
    string Status,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<FollowUpRevisionDto> Revisions,
    IReadOnlyList<FollowUpTimelineDto> Timeline,
    bool TimelineTruncated);

internal sealed record FollowUpRevisionDto(
    string RevisionId,
    string Field,
    string Value,
    string State,
    IReadOnlyList<string> EvidenceRefs,
    DateTimeOffset SourceTimestamp,
    string ParserVersion,
    decimal Confidence,
    string? CorrectionEvidenceRef,
    IReadOnlyList<string> LineageRevisionRefs,
    DateTimeOffset CreatedAt);

internal sealed record FollowUpTimelineDto(
    long Sequence,
    string Kind,
    string? Field,
    string Summary,
    string EvidenceRef,
    DateTimeOffset SourceTimestamp,
    DateTimeOffset RecordedAt);

internal sealed record FollowUpWhyDto(
    string FollowUpId,
    IReadOnlyDictionary<string, FollowUpRevisionDto[]> Fields,
    bool Truncated);

internal sealed record ContinuityErrorDto(string Code, string Message);