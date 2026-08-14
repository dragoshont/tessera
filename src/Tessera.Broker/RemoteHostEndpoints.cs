using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Tessera.Core.Configuration;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Tessera.Identity;
using Tessera.Persistence.Sqlite;

namespace Tessera.Broker;

public static class RemoteHostEndpoints
{
    private static readonly JsonSerializerOptions PublicJson = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions StrictJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };

    public static void MapRemoteHostEndpoints(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/v1/host-pairings")
                || context.Request.Path.StartsWithSegments("/api/v1/hosts")
                || context.Request.Path.StartsWithSegments("/api/v1/host-artifacts")
                || context.Request.Path.StartsWithSegments("/api/v1/jobs")
                || context.Request.Path.StartsWithSegments("/api/v1/job-runs")
                || context.Request.Path.StartsWithSegments("/host-channel"))
                context.Response.Headers.CacheControl = "no-store";
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
            {
                if (IsRemoteHostArtifactUpload(context.Request))
                    bodyLimit.MaxRequestBodySize = RemoteHostProtocol.MaximumArtifactRequestBodyBytes;
                else if (IsRemoteHostMutation(context.Request))
                    bodyLimit.MaxRequestBodySize = RemoteHostProtocol.MaximumBodyBytes;
            }
            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
                when ((context.Request.Path.StartsWithSegments("/api/v1/host-pairings")
                    || context.Request.Path.StartsWithSegments("/api/v1/hosts")
                    || context.Request.Path.StartsWithSegments("/api/v1/host-artifacts")
                    || context.Request.Path.StartsWithSegments("/api/v1/jobs")
                    || context.Request.Path.StartsWithSegments("/api/v1/job-runs")
                    || context.Request.Path.StartsWithSegments("/host-channel"))
                    && !context.Response.HasStarted)
            {
                context.Response.Clear();
                await Problem(503, "product_storage_unavailable").ExecuteAsync(context).ConfigureAwait(false);
            }
        });

        app.MapPost("/api/v1/host-pairings", async (
            HttpContext context, HostPairingCreateRequest? request,
            ITokenValidator validator, TesseraConfig config,
            IServiceProvider services, CancellationToken token) =>
        {
            var boundary = await BoundaryAsync(context, validator, config, services, token).ConfigureAwait(false);
            if (boundary.Error is not null) return boundary.Error;
            if (!IsJson(context) || !Valid(request) || IdempotencyKey(context) is not { } key)
                return Problem(400, "pairing_invalid_request");
            try { RemoteHostValidation.ValidateLowerHex(request!.ClaimSecretHash, 64, nameof(request.ClaimSecretHash)); }
            catch (ArgumentException) { return Problem(400, "pairing_invalid_request"); }
            var now = DateTimeOffset.UtcNow;
            var pairingId = $"pairing-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))}";
            var expiresAt = now.Add(RemoteHostValidation.MaximumPairingTtl);
            var requestHash = Hash(request);
            var result = await boundary.Store!.CreateHostPairingAsync(
                boundary.Owner!, pairingId, request.ClaimSecretHash, key, requestHash,
                now, expiresAt, token).ConfigureAwait(false);
            if (result.Receipt is null) return Problem(409, result.Error!);
            NoStore(context);
            return Results.Text(result.Receipt!.ResponseBodyJson, "application/json", Encoding.UTF8,
                result.Receipt.ResponseStatus);
        });

        app.MapGet("/api/v1/host-pairings/{pairingId}", async (
            HttpContext context, string pairingId, ITokenValidator validator, TesseraConfig config,
            IServiceProvider services, CancellationToken token) =>
        {
            var boundary = await BoundaryAsync(context, validator, config, services, token).ConfigureAwait(false);
            if (boundary.Error is not null) return boundary.Error;
            var pairing = await boundary.Store!.GetHostPairingAsync(boundary.Owner!, pairingId, token).ConfigureAwait(false);
            if (pairing is null) return Problem(404, "pairing_not_found");
            NoStore(context);
            return Results.Json(PairingDto(pairing));
        });

        app.MapPost("/api/v1/host-pairings/{pairingId}/claim", async (
            HttpContext context, string pairingId, HostClaimRequest? request,
            SqliteKernelStore store, CancellationToken token) =>
        {
            if (!IsJson(context) || !Valid(request) || IdempotencyKey(context) is not { } key)
                return Problem(400, "pairing_invalid_request");
            var requestHash = Hash(request!);
            try
            {
                var publicKey = RemoteHostValidation.NormalizeP256PublicJwk(request!.PublicKeyJwk.GetRawText());
                var claim = new HostClaim(publicKey, request.Protection, request.Platform, request.Architecture,
                    request.AgentVersion, request.ProtocolVersion,
                    request.RequestedCapabilities.Select(item => new RequestedHostCapability(
                        item.CapabilityId, item.CapabilityVersion, item.SchemaHash, item.SideEffectClass)).ToArray(),
                    request.RequestedResources.Select(item => new RequestedHostResource(
                        item.ResourceId, item.Type, item.DisplayName, item.Fingerprint, item.State)).ToArray());
                var result = await store.ClaimHostPairingAsync(
                    pairingId, request.ClaimSecret, claim, key, requestHash, DateTimeOffset.UtcNow, token)
                    .ConfigureAwait(false);
                if (result.Receipt is null) return PairingProblem(result.Error!);
                NoStore(context);
                return Results.Text(result.Receipt.ResponseBodyJson, "application/json", Encoding.UTF8,
                    result.Receipt.ResponseStatus);
            }
            catch (ArgumentException) { return Problem(400, "pairing_invalid_request"); }
            catch (JsonException) { return Problem(400, "pairing_invalid_request"); }
        });

        app.MapPost("/api/v1/host-pairings/{pairingId}/confirm", async (
            HttpContext context, string pairingId, HostConfirmRequest? request,
            ITokenValidator validator, TesseraConfig config, IServiceProvider services,
            CancellationToken token) =>
        {
            var boundary = await BoundaryAsync(context, validator, config, services, token).ConfigureAwait(false);
            if (boundary.Error is not null) return boundary.Error;
            if (!IsJson(context) || !Valid(request) || IdempotencyKey(context) is not { } key)
                return Problem(400, "pairing_invalid_request");
            var requestHash = Hash(request!);
            try
            {
                var hostId = $"host-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))}";
                var result = await boundary.Store!.ConfirmHostPairingAsync(
                    boundary.Owner!, pairingId, request!.ExpectedVersion, request.ConfirmationCode,
                    hostId, request.DisplayName,
                    request.CapabilityGrants.Select(item => new HostCapabilityGrantRequest(item.CapabilityId, item.CapabilityVersion)).ToArray(),
                    request.ResourceGrants.Select(item => new HostResourceGrantRequest(item.ResourceId, item.AccessMode)).ToArray(),
                    key, requestHash, DateTimeOffset.UtcNow, token).ConfigureAwait(false);
                if (result.Receipt is null) return PairingProblem(result.Error!);
                NoStore(context);
                return Results.Text(result.Receipt.ResponseBodyJson, "application/json", Encoding.UTF8,
                    result.Receipt.ResponseStatus);
            }
            catch (ArgumentException) { return Problem(400, "pairing_invalid_request"); }
            catch (Microsoft.Data.Sqlite.SqliteException) { return Problem(503, "product_storage_unavailable"); }
        });

        app.MapPost("/api/v1/host-pairings/{pairingId}/cancel", async (
            HttpContext context, string pairingId, VersionRequest? request,
            ITokenValidator validator, TesseraConfig config, IServiceProvider services,
            CancellationToken token) =>
        {
            var boundary = await BoundaryAsync(context, validator, config, services, token).ConfigureAwait(false);
            if (boundary.Error is not null) return boundary.Error;
            if (!IsJson(context) || !Valid(request) || IdempotencyKey(context) is not { } key)
                return Problem(400, "pairing_invalid_request");
            var requestHash = Hash(request!);
            var result = await boundary.Store!.CancelHostPairingAsync(
                boundary.Owner!, pairingId, request!.ExpectedVersion, key, requestHash,
                DateTimeOffset.UtcNow, token).ConfigureAwait(false);
            if (result.Receipt is null) return PairingProblem(result.Error!);
            NoStore(context);
            return Results.Text(result.Receipt.ResponseBodyJson, "application/json", Encoding.UTF8,
                result.Receipt.ResponseStatus);
        });

        app.MapGet("/api/v1/hosts", async (
            HttpContext context, ITokenValidator validator, TesseraConfig config,
            IServiceProvider services, CancellationToken token) =>
        {
            var boundary = await BoundaryAsync(context, validator, config, services, token).ConfigureAwait(false);
            if (boundary.Error is not null) return boundary.Error;
            var hosts = await boundary.Store!.ListRemoteHostsAsync(boundary.Owner!, token).ConfigureAwait(false);
            NoStore(context);
            return Results.Json(new { items = hosts.Select(HostSummaryDto).ToArray(), nextCursor = (string?)null });
        });

        app.MapGet("/api/v1/hosts/{hostId}", async (
            HttpContext context, string hostId, ITokenValidator validator, TesseraConfig config,
            IServiceProvider services, CancellationToken token) =>
        {
            var boundary = await BoundaryAsync(context, validator, config, services, token).ConfigureAwait(false);
            if (boundary.Error is not null) return boundary.Error;
            var host = await boundary.Store!.GetRemoteHostDetailAsync(boundary.Owner!, hostId, token).ConfigureAwait(false);
            if (host is null) return Problem(404, "host_not_found");
            NoStore(context);
            return Results.Json(HostDetailDto(host));
        });

        app.MapPut("/api/v1/hosts/{hostId}/grants", async (
            HttpContext context, string hostId, HostGrantsRequest? request,
            ITokenValidator validator, TesseraConfig config, IServiceProvider services,
            CancellationToken token) =>
        {
            var boundary = await BoundaryAsync(context, validator, config, services, token).ConfigureAwait(false);
            if (boundary.Error is not null) return boundary.Error;
            if (!IsJson(context) || !Valid(request) || IdempotencyKey(context) is not { } key)
                return Problem(400, "host_invalid_request");
            var requestHash = Hash(request!);
            try
            {
                var result = await boundary.Store!.UpdateRemoteHostGrantsAsync(
                    boundary.Owner!, hostId, request!.ExpectedVersion,
                    request.CapabilityGrants.Select(item => new HostCapabilityGrantRequest(item.CapabilityId, item.CapabilityVersion)).ToArray(),
                    request.ResourceGrants.Select(item => new HostResourceGrantRequest(item.ResourceId, item.AccessMode)).ToArray(),
                    key, requestHash, DateTimeOffset.UtcNow, token).ConfigureAwait(false);
                if (result.Receipt is null) return HostProblem(result.Error!);
                NoStore(context);
                return Results.Text(result.Receipt.ResponseBodyJson, "application/json", Encoding.UTF8,
                    result.Receipt.ResponseStatus);
            }
            catch (ArgumentException) { return Problem(400, "host_invalid_request"); }
            catch (Microsoft.Data.Sqlite.SqliteException) { return Problem(503, "product_storage_unavailable"); }
        });

        app.MapPost("/api/v1/hosts/{hostId}/revoke", async (
            HttpContext context, string hostId, VersionRequest? request,
            ITokenValidator validator, TesseraConfig config, IServiceProvider services,
            CancellationToken token) =>
        {
            var boundary = await BoundaryAsync(context, validator, config, services, token).ConfigureAwait(false);
            if (boundary.Error is not null) return boundary.Error;
            if (!IsJson(context) || !Valid(request) || IdempotencyKey(context) is not { } key)
                return Problem(400, "host_invalid_request");
            var requestHash = Hash(request!);
            var result = await boundary.Store!.RevokeRemoteHostAsync(
                boundary.Owner!, hostId, request!.ExpectedVersion, key, requestHash,
                DateTimeOffset.UtcNow, token).ConfigureAwait(false);
            if (result.Receipt is null) return HostProblem(result.Error!);
            NoStore(context);
            return Results.Text(result.Receipt.ResponseBodyJson, "application/json", Encoding.UTF8,
                result.Receipt.ResponseStatus);
        });

        app.MapGet("/api/v1/jobs/{jobId}/execution-policy", async (
            HttpContext context, string jobId, ITokenValidator validator, TesseraConfig config,
            IServiceProvider services, CancellationToken token) =>
        {
            var boundary = await BoundaryAsync(context, validator, config, services, token).ConfigureAwait(false);
            if (boundary.Error is not null) return boundary.Error;
            if (await boundary.Store!.GetJobAsync(boundary.Owner!, jobId, token).ConfigureAwait(false) is null)
                return Problem(404, "not_found");
            var policy = await boundary.Store.GetJobExecutionPolicyAsync(boundary.Owner!, jobId, token).ConfigureAwait(false);
            NoStore(context);
            return Results.Json(policy is null ? DefaultExecutionPolicy(jobId) : ExecutionPolicyDto(policy));
        });

        app.MapPut("/api/v1/jobs/{jobId}/execution-policy", async (
            HttpContext context, string jobId, [FromBody] HostExecutionPolicyRequest? request,
            ITokenValidator validator, TesseraConfig config, IServiceProvider services,
            CancellationToken token) =>
        {
            var boundary = await BoundaryAsync(context, validator, config, services, token).ConfigureAwait(false);
            if (boundary.Error is not null) return boundary.Error;
            if (!IsJson(context) || !Valid(request) || IdempotencyKey(context) is not { } key)
                return Problem(400, "host_invalid_request");
            if (await boundary.Store!.GetJobAsync(boundary.Owner!, jobId, token).ConfigureAwait(false) is null)
                return Problem(404, "not_found");
            try
            {
                var policy = new JobExecutionPolicy(
                    boundary.Owner!,
                    jobId,
                    request!.Location,
                    request.PreferredHostId,
                    request.RequiredCapabilities.Select(item => (item.CapabilityId, item.CapabilityVersion)).ToArray(),
                    request.RequiredResourceIds,
                    request.FallbackPolicy,
                    request.ExpectedVersion + 1);
                var saved = await boundary.Store.PutJobExecutionPolicyWithReceiptAsync(
                    policy, request.ExpectedVersion, key, Hash(request), DateTimeOffset.UtcNow, token).ConfigureAwait(false);
                if (saved.Receipt is null) return Problem(409, saved.Error!);
                NoStore(context);
                return Results.Text(saved.Receipt.ResponseBodyJson, "application/json", Encoding.UTF8,
                    saved.Receipt.ResponseStatus);
            }
            catch (ArgumentException) { return Problem(400, "host_invalid_request"); }
            catch (InvalidOperationException) { return Problem(400, "host_invalid_request"); }
        });

        app.MapDelete("/api/v1/jobs/{jobId}/execution-policy", async (
            HttpContext context, string jobId, [FromBody] VersionRequest? request,
            ITokenValidator validator, TesseraConfig config, IServiceProvider services,
            CancellationToken token) =>
        {
            var boundary = await BoundaryAsync(context, validator, config, services, token).ConfigureAwait(false);
            if (boundary.Error is not null) return boundary.Error;
            if (!IsJson(context) || !Valid(request) || IdempotencyKey(context) is not { } key)
                return Problem(400, "host_invalid_request");
            if (await boundary.Store!.GetJobAsync(boundary.Owner!, jobId, token).ConfigureAwait(false) is null)
                return Problem(404, "not_found");
            var deleted = await boundary.Store.DeleteJobExecutionPolicyWithReceiptAsync(
                boundary.Owner!, jobId, request!.ExpectedVersion, key, Hash(request), DateTimeOffset.UtcNow, token)
                .ConfigureAwait(false);
            if (deleted.Receipt is null) return Problem(409, deleted.Error!);
            NoStore(context);
            return Results.Text(deleted.Receipt.ResponseBodyJson, "application/json", Encoding.UTF8,
                deleted.Receipt.ResponseStatus);
        });

        app.MapGet("/api/v1/job-runs/{runId}/remote", async (
            HttpContext context, string runId, ITokenValidator validator, TesseraConfig config,
            IServiceProvider services, CancellationToken token) =>
        {
            var boundary = await BoundaryAsync(context, validator, config, services, token).ConfigureAwait(false);
            if (boundary.Error is not null) return boundary.Error;
            var run = await boundary.Store!.GetJobRunAsync(boundary.Owner!, runId, token).ConfigureAwait(false);
            if (run is null) return Problem(404, "not_found");
            var projection = await boundary.Store.GetRemoteJobRunProjectionAsync(boundary.Owner!, runId, token).ConfigureAwait(false);
            NoStore(context);
            return Results.Json(new
            {
                blocker = projection?.Blocker is null ? null : BlockerDto(projection.Blocker),
                lease = projection?.Lease is null ? null : LeaseDto(projection.Lease),
                host = projection?.Host is null ? null : HostSummaryDto(projection.Host),
                checkpoints = projection?.Checkpoints.Select(item => new { item.Sequence, item.Step, item.StateJson, item.Fence, item.CreatedAt }).ToArray() ?? [],
                artifacts = projection?.Artifacts.Select(ArtifactSummaryDto).ToArray() ?? [],
            });
        });

        app.MapGet("/api/v1/job-runs/{runId}/remote-artifacts", async (
            HttpContext context, string runId, int? limit, string? cursor,
            ITokenValidator validator, TesseraConfig config,
            IServiceProvider services, CancellationToken token) =>
        {
            var boundary = await BoundaryAsync(context, validator, config, services, token).ConfigureAwait(false);
            if (boundary.Error is not null) return boundary.Error;
            if (await boundary.Store!.GetJobRunAsync(boundary.Owner!, runId, token).ConfigureAwait(false) is null)
                return Problem(404, "not_found");
            try
            {
                var page = await boundary.Store.ListRunHostArtifactsAsync(boundary.Owner!, runId, limit, cursor, token).ConfigureAwait(false);
                NoStore(context);
                return Results.Json(new
                {
                    items = page.Items.Select(ArtifactSummaryDto).ToArray(),
                    page.NextCursor,
                });
            }
            catch (ArgumentException)
            {
                return Problem(400, "invalid_cursor");
            }
        });

        app.MapGet("/api/v1/host-artifacts/{artifactId}", async (
            HttpContext context, string artifactId, ITokenValidator validator, TesseraConfig config,
            IServiceProvider services, CancellationToken token) =>
        {
            var boundary = await BoundaryAsync(context, validator, config, services, token).ConfigureAwait(false);
            if (boundary.Error is not null) return boundary.Error;
            HostArtifactDetail? artifact;
            try
            {
                artifact = await boundary.Store!.GetHostArtifactDetailAsync(boundary.Owner!, artifactId, token).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                return Problem(400, "artifact_invalid_request");
            }
            if (artifact is null) return Problem(404, "artifact_not_found");
            NoStore(context);
            return Results.Json(ArtifactDetailDto(artifact));
        });

        app.MapPost("/api/v1/host-artifacts/{artifactId}/verify", async (
            HttpContext context, string artifactId, VersionRequest? request,
            ITokenValidator validator, TesseraConfig config, IServiceProvider services,
            CancellationToken token) =>
        {
            var boundary = await BoundaryAsync(context, validator, config, services, token).ConfigureAwait(false);
            if (boundary.Error is not null) return boundary.Error;
            if (!IsJson(context) || !Valid(request) || IdempotencyKey(context) is not { } key)
                return Problem(400, "artifact_invalid_request");
            HostArtifactVerifyReceiptMutation result;
            try
            {
                result = await boundary.Store!.VerifyHostArtifactAsync(
                    boundary.Owner!, artifactId, request!.ExpectedVersion, key, Hash(request), DateTimeOffset.UtcNow, token)
                    .ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                return Problem(400, "artifact_invalid_request");
            }
            if (result.Receipt is null) return ArtifactProblem(result.Error!);
            NoStore(context);
            return Results.Text(result.Receipt.ResponseBodyJson, "application/json", Encoding.UTF8,
                result.Receipt.ResponseStatus);
        });

        app.MapPost("/host-channel/poll", async (HttpContext context, SqliteKernelStore store, CancellationToken token) =>
        {
            if (!IsJson(context)) return Problem(415, "invalid_media_type");
            var read = await RemoteHostRequestReader.ReadAsync(context.Request, HostAcceptedMessageOperations.Poll, "-", token).ConfigureAwait(false);
            if (!read.Succeeded) return HostSignedProblem(read.Error!);
            HostPollRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<HostPollRequest>(read.Body, StrictJson);
            }
            catch (JsonException)
            {
                return HostSignedResult(await AcceptSignedInvalidRequestAsync(store, read.Envelope!, token).ConfigureAwait(false));
            }
            if (!Valid(request)) return HostSignedResult(await AcceptSignedInvalidRequestAsync(store, read.Envelope!, token).ConfigureAwait(false));
            var result = await store.AcceptSignedHostMessageAsync(
                read.Envelope!,
                DateTimeOffset.UtcNow,
                (connection, transaction, host, ct) => SqliteKernelStore.PollHostAsync(
                    connection,
                    transaction,
                    host,
                    request!.MaxWaitSeconds,
                    request.ActiveAttempt is null ? null : new HostPollActiveAttempt(
                        request.ActiveAttempt.LeaseId,
                        request.ActiveAttempt.LocalAttemptId,
                        request.ActiveAttempt.State),
                    DateTimeOffset.UtcNow,
                    ct),
                token).ConfigureAwait(false);
            return HostSignedResult(result);
        });

        app.MapPost("/host-channel/leases/{leaseId}/ack", async (HttpContext context, string leaseId, SqliteKernelStore store, CancellationToken token) =>
        {
            if (!IsJson(context)) return Problem(415, "invalid_media_type");
            var read = await RemoteHostRequestReader.ReadAsync(context.Request, HostAcceptedMessageOperations.LeaseAck, leaseId, token).ConfigureAwait(false);
            if (!read.Succeeded) return HostSignedProblem(read.Error!);
            HostLeaseAckRequest? request;
            try { request = JsonSerializer.Deserialize<HostLeaseAckRequest>(read.Body, StrictJson); }
            catch (JsonException) { return HostSignedResult(await AcceptSignedInvalidRequestAsync(store, read.Envelope!, token).ConfigureAwait(false)); }
            if (!Valid(request)) return HostSignedResult(await AcceptSignedInvalidRequestAsync(store, read.Envelope!, token).ConfigureAwait(false));
            var result = await store.AcceptSignedHostMessageAsync(
                read.Envelope!,
                DateTimeOffset.UtcNow,
                (connection, transaction, host, ct) => SqliteKernelStore.AcknowledgeHostLeaseAsync(
                    connection, transaction, host, leaseId, request!.LeaseVersion, request.LocalAttemptId,
                    request.Accepted, request.RejectionCode, DateTimeOffset.UtcNow, ct),
                token).ConfigureAwait(false);
            return HostSignedResult(result);
        });

        app.MapPost("/host-channel/leases/{leaseId}/events", async (HttpContext context, string leaseId, SqliteKernelStore store, CancellationToken token) =>
        {
            if (!IsJson(context)) return Problem(415, "invalid_media_type");
            var read = await RemoteHostRequestReader.ReadAsync(context.Request, HostAcceptedMessageOperations.LeaseEvents, leaseId, token).ConfigureAwait(false);
            if (!read.Succeeded) return HostSignedProblem(read.Error!);
            HostLeaseEventsRequest? request;
            try { request = JsonSerializer.Deserialize<HostLeaseEventsRequest>(read.Body, StrictJson); }
            catch (JsonException) { return HostSignedResult(await AcceptSignedInvalidRequestAsync(store, read.Envelope!, token).ConfigureAwait(false)); }
            if (!Valid(request)) return HostSignedResult(await AcceptSignedInvalidRequestAsync(store, read.Envelope!, token).ConfigureAwait(false));
            var events = request!.Events.Select(item => new HostLeaseEvent(
                string.Empty,
                leaseId,
                item.EventId,
                item.Sequence,
                item.Type,
                item.OccurredAt,
                item.Summary,
                item.Data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : item.Data.GetRawText())).ToArray();
            var result = await store.AcceptSignedHostMessageAsync(
                read.Envelope!,
                DateTimeOffset.UtcNow,
                (connection, transaction, host, ct) => SqliteKernelStore.AppendHostLeaseEventsAsync(
                    connection, transaction, host, leaseId, request.LeaseVersion, request.LocalAttemptId,
                    events, DateTimeOffset.UtcNow, ct),
                token).ConfigureAwait(false);
            return HostSignedResult(result);
        });

        app.MapPost("/host-channel/leases/{leaseId}/complete", async (HttpContext context, string leaseId, SqliteKernelStore store, CancellationToken token) =>
        {
            if (!IsJson(context)) return Problem(415, "invalid_media_type");
            var read = await RemoteHostRequestReader.ReadAsync(context.Request, HostAcceptedMessageOperations.LeaseComplete, leaseId, token).ConfigureAwait(false);
            if (!read.Succeeded) return HostSignedProblem(read.Error!);
            HostLeaseCompleteRequest? request;
            try { request = JsonSerializer.Deserialize<HostLeaseCompleteRequest>(read.Body, StrictJson); }
            catch (JsonException) { return HostSignedResult(await AcceptSignedInvalidRequestAsync(store, read.Envelope!, token).ConfigureAwait(false)); }
            if (!Valid(request)) return HostSignedResult(await AcceptSignedInvalidRequestAsync(store, read.Envelope!, token).ConfigureAwait(false));
            var result = await store.AcceptSignedHostMessageAsync(
                read.Envelope!,
                DateTimeOffset.UtcNow,
                (connection, transaction, host, ct) => SqliteKernelStore.CompleteHostLeaseAsync(
                    connection, transaction, host, leaseId, request!.LeaseVersion, request.LocalAttemptId,
                    request.Outcome, request.Output, request.OutputSha256, request.Truncated, DateTimeOffset.UtcNow, ct),
                token).ConfigureAwait(false);
            return HostSignedResult(result);
        });

        app.MapPost("/host-channel/leases/{leaseId}/reconcile", async (HttpContext context, string leaseId, SqliteKernelStore store, CancellationToken token) =>
        {
            if (!IsJson(context)) return Problem(415, "invalid_media_type");
            var read = await RemoteHostRequestReader.ReadAsync(context.Request, HostAcceptedMessageOperations.LeaseReconcile, leaseId, token).ConfigureAwait(false);
            if (!read.Succeeded) return HostSignedProblem(read.Error!);
            HostLeaseReconcileRequest? request;
            try { request = JsonSerializer.Deserialize<HostLeaseReconcileRequest>(read.Body, StrictJson); }
            catch (JsonException) { return HostSignedResult(await AcceptSignedInvalidRequestAsync(store, read.Envelope!, token).ConfigureAwait(false)); }
            if (!Valid(request)) return HostSignedResult(await AcceptSignedInvalidRequestAsync(store, read.Envelope!, token).ConfigureAwait(false));
            var result = await store.AcceptSignedHostMessageAsync(
                read.Envelope!,
                DateTimeOffset.UtcNow,
                (connection, transaction, host, ct) => SqliteKernelStore.ReconcileHostLeaseAsync(
                    connection, transaction, host, leaseId, request!.LeaseVersion, request.LocalAttemptId,
                    request.ObservedState, request.OutputSha256, DateTimeOffset.UtcNow, ct),
                token).ConfigureAwait(false);
            return HostSignedResult(result);
        });

        app.MapPost("/host-channel/leases/{leaseId}/artifacts", async (HttpContext context, string leaseId, SqliteKernelStore store, CancellationToken token) =>
        {
            if (!IsJson(context)) return Problem(415, "invalid_media_type");
            var read = await RemoteHostRequestReader.ReadAsync(
                context.Request,
                HostAcceptedMessageOperations.LeaseArtifact,
                leaseId,
                RemoteHostProtocol.MaximumArtifactRequestBodyBytes,
                token).ConfigureAwait(false);
            if (!read.Succeeded) return HostSignedProblem(read.Error!);
            HostLeaseArtifactRequest? request;
            try { request = JsonSerializer.Deserialize<HostLeaseArtifactRequest>(read.Body, StrictJson); }
            catch (JsonException) { return HostSignedResult(await AcceptSignedInvalidRequestAsync(store, read.Envelope!, token).ConfigureAwait(false)); }
            if (!Valid(request)) return HostSignedResult(await AcceptSignedInvalidRequestAsync(store, read.Envelope!, token).ConfigureAwait(false));
            var result = await store.AcceptSignedHostMessageAsync(
                read.Envelope!,
                DateTimeOffset.UtcNow,
                (connection, transaction, host, ct) => SqliteKernelStore.UploadHostArtifactAsync(
                    connection,
                    transaction,
                    host,
                    leaseId,
                    request!.LeaseVersion,
                    request.LocalAttemptId!,
                    request.ArtifactId!,
                    request.Kind!,
                    request.MediaType!,
                    request.Summary!,
                    request.DeclaredSize,
                    request.DeclaredSha256!,
                    request.Retention!,
                    request.TextContent!,
                    read.Envelope!.MessageId,
                    DateTimeOffset.UtcNow,
                    ct),
                token).ConfigureAwait(false);
            return HostSignedResult(result);
        });
    }

    private static async Task<RemoteBoundary> BoundaryAsync(
        HttpContext context, ITokenValidator validator, TesseraConfig config,
        IServiceProvider services, CancellationToken token)
    {
        var user = await PortalEndpoints.ResolveEndUserAsync(context, validator, config).ConfigureAwait(false);
        if (user?.CanonicalPrincipalId is null || string.IsNullOrWhiteSpace(user.TenantId))
            return new(null, null, Problem(401, "unauthenticated"));
        var store = services.GetService<SqliteKernelStore>();
        if (store is null) return new(null, null, Problem(503, "product_storage_unavailable"));
        var principal = PrincipalRef.Create(user.Issuer, user.TenantId, user.Subject,
            user.PreferredUsername, DateTimeOffset.UtcNow);
        await store.AddAsync(principal, token).ConfigureAwait(false);
        return new(store, principal.PrincipalId, null);
    }

    private static object PairingDto(HostPairing pairing) => new
    {
        pairingId = pairing.PairingId,
        state = pairing.State,
        requestedHost = pairing.RequestedHost is null ? null : new
        {
            protection = pairing.RequestedHost.Protection,
            platform = pairing.RequestedHost.Platform,
            architecture = pairing.RequestedHost.Architecture,
            agentVersion = pairing.RequestedHost.AgentVersion,
            protocolVersion = pairing.RequestedHost.ProtocolVersion,
            requestedCapabilities = pairing.RequestedHost.RequestedCapabilities,
            requestedResources = pairing.RequestedHost.RequestedResources,
        },
        expiresAt = pairing.ExpiresAt,
        version = pairing.Version,
    };

    private static object HostSummaryDto(RemoteHost host) => new
    {
        hostId = host.HostId,
        displayName = host.DisplayName,
        platform = host.Platform,
        architecture = host.Architecture,
        lifecycle = host.Lifecycle,
        connectionStatus = host.ConnectionStatus,
        agentVersion = host.AgentVersion,
        protocolVersion = host.ProtocolVersion,
        lastSeenAt = host.LastSeenAt,
        pairedAt = host.PairedAt,
        revokedAt = host.RevokedAt,
        version = host.Version,
    };

    private static object HostDetailDto(RemoteHostDetail detail) => new
    {
        host = HostSummaryDto(detail.Host),
        capabilities = detail.Capabilities.Select(item => new
        {
            item.CapabilityId, item.CapabilityVersion, item.SchemaHash, item.SideEffectClass, item.AdvertisedAt,
        }).ToArray(),
        capabilityGrants = detail.CapabilityGrants.Select(item => new
        {
            item.CapabilityId, item.CapabilityVersion, item.GrantedAt, item.RevokedAt, item.Version,
        }).ToArray(),
        resources = detail.Resources.Select(item => new
        {
            item.ResourceId, item.Type, item.DisplayName, item.Fingerprint, item.State, item.AdvertisedAt, item.Version,
        }).ToArray(),
        resourceGrants = detail.ResourceGrants.Select(item => new
        {
            item.ResourceId, item.AccessMode, item.GrantedAt, item.RevokedAt, item.Version,
        }).ToArray(),
    };

    private static object ExecutionPolicyDto(JobExecutionPolicy policy) => new
    {
        jobId = policy.JobId,
        policy.Location,
        policy.PreferredHostId,
        requiredCapabilities = policy.RequiredCapabilities.Select(item => new { capabilityId = item.Id, capabilityVersion = item.Version }).ToArray(),
        requiredResourceIds = policy.RequiredResourceIds,
        fallbackPolicy = policy.FallbackPolicy,
        policy.Version,
    };

    private static object DefaultExecutionPolicy(string jobId) => new
    {
        jobId,
        Location = JobExecutionLocations.Server,
        PreferredHostId = (string?)null,
        requiredCapabilities = Array.Empty<object>(),
        requiredResourceIds = Array.Empty<string>(),
        fallbackPolicy = JobExecutionFallbackPolicies.None,
        Version = 0L,
    };

    private static object BlockerDto(JobRunBlocker blocker) => new
    {
        blocker.Code,
        blocker.HostId,
        blocker.CapabilityId,
        blocker.ResourceId,
        blocker.DetailCode,
        blocker.ObservedAt,
        blocker.ClearedAt,
        blocker.Version,
    };

    private static object LeaseDto(HostWorkLease lease) => new
    {
        leaseId = lease.LeaseId,
        leaseVersion = lease.Version,
        lease.RunId,
        lease.JobId,
        lease.HostId,
        lease.SchedulerFence,
        lease.Attempt,
        lease.ProfileId,
        lease.CapabilityId,
        lease.CapabilityVersion,
        lease.CapabilityGrantVersion,
        lease.InputHash,
        lease.State,
        lease.IssuedAt,
        lease.ExecuteUntil,
        lease.AcknowledgedAt,
        lease.CompletedAt,
        lease.LocalAttemptId,
        lease.Outcome,
        lease.OutputSha256,
        lease.FailureCode,
    };

    private static object ArtifactSummaryDto(HostArtifact artifact) => new
    {
        artifactId = artifact.ArtifactId,
        runId = artifact.RunId,
        leaseId = artifact.LeaseId,
        actionId = artifact.ActionId,
        kind = artifact.Kind,
        mediaType = artifact.MediaType,
        summary = artifact.Summary,
        sizeBytes = artifact.SizeBytes,
        sha256 = artifact.Sha256,
        retention = artifact.Retention,
        contentState = artifact.ContentState,
        redacted = artifact.Redacted,
        truncated = artifact.Truncated,
        createdAt = artifact.CreatedAt,
        expiresAt = artifact.ExpiresAt,
        version = artifact.Version,
    };

    private static object ArtifactDetailDto(HostArtifactDetail artifact) => new
    {
        artifact = ArtifactSummaryDto(artifact.Artifact),
        textContent = artifact.TextContent,
    };

    private static bool IsRemoteHostMutation(HttpRequest request)
        => HttpMethods.IsPost(request.Method)
            && (request.Path.StartsWithSegments("/api/v1/host-pairings")
                || request.Path.StartsWithSegments("/api/v1/hosts")
                || request.Path.StartsWithSegments("/api/v1/host-artifacts")
                || request.Path.StartsWithSegments("/host-channel"))
            || HttpMethods.IsPut(request.Method)
            && (request.Path.StartsWithSegments("/api/v1/hosts")
                || request.Path.StartsWithSegments("/api/v1/jobs"))
            || HttpMethods.IsDelete(request.Method)
            && request.Path.StartsWithSegments("/api/v1/jobs");

    private static bool IsRemoteHostArtifactUpload(HttpRequest request)
        => HttpMethods.IsPost(request.Method)
            && request.Path.StartsWithSegments("/host-channel/leases")
            && request.Path.Value?.EndsWith("/artifacts", StringComparison.Ordinal) == true;

    private static IResult PairingProblem(string code) => Problem(code switch
    {
        "pairing_not_found" => 404,
        "pairing_invalid_request" => 400,
        "pairing_expired" or "pairing_canceled" or "pairing_consumed" or
        "pairing_attempts_exceeded" or "pairing_confirmation_mismatch" or
        "pairing_grant_not_requested" or "pairing_version_conflict" or
        "idempotency_conflict" => 409,
        _ => 400,
    }, code);

    private static IResult HostProblem(string code) => Problem(code switch
    {
        "host_not_found" => 404,
        "host_version_conflict" or "host_revoked" or "host_grant_not_advertised" or
        "idempotency_conflict" => 409,
        _ => 400,
    }, code);

    private static IResult ArtifactProblem(string code) => Problem(code switch
    {
        "artifact_not_found" => 404,
        "artifact_version_conflict" or "artifact_conflict" or "artifact_hash_mismatch" or
        "idempotency_conflict" => 409,
        _ => 400,
    }, code);

    private static IResult HostSignedProblem(string code)
        => Problem(RemoteHostSignedRequestErrors.StatusCode(code), code);

    private static Task<HostMessageAcceptanceResult> AcceptSignedInvalidRequestAsync(
        SqliteKernelStore store,
        HostSignedRequestEnvelope envelope,
        CancellationToken token)
        => store.AcceptSignedHostMessageAsync(
            envelope,
            DateTimeOffset.UtcNow,
            (_, _, _, _) => Task.FromResult(new HostMessageBusinessResponse(
                400,
                RemoteHostSnapshotSerializer.SerializeProblem(400, "host_invalid_request"))),
            token);

    private static IResult HostSignedResult(HostMessageAcceptanceResult result)
    {
        if (!result.Succeeded)
            return HostSignedProblem(result.Error!);
        return Results.Text(result.Receipt!.ResponseBodyJson, "application/json", Encoding.UTF8, result.Receipt.ResponseStatus);
    }

    private static string Hash<T>(T request)
        => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, PublicJson)));

    private static string? IdempotencyKey(HttpContext context)
    {
        var value = context.Request.Headers["Idempotency-Key"].FirstOrDefault();
        try { RemoteHostValidation.ValidateIdentifier(value ?? string.Empty, "Idempotency-Key"); return value; }
        catch (ArgumentException) { return null; }
    }

    private static bool IsJson(HttpContext context)
        => context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true;

    private static bool Valid(HostClaimRequest? request)
        => ValidRequest(request)
            && request!.PublicKeyJwk.ValueKind == JsonValueKind.Object
            && request.RequestedCapabilities is not null
            && request.RequestedResources is not null
            && request.RequestedCapabilities.All(ValidRequest)
            && request.RequestedResources.All(ValidRequest);

    private static bool Valid(HostPairingCreateRequest? request)
        => ValidRequest(request) && !string.IsNullOrWhiteSpace(request!.ClaimSecretHash);

    private static bool Valid(HostConfirmRequest? request)
        => ValidRequest(request)
            && request!.ExpectedVersion >= 1
            && request.CapabilityGrants is not null
            && request.ResourceGrants is not null
            && request.CapabilityGrants.All(ValidRequest)
            && request.ResourceGrants.All(ValidRequest);

    private static bool Valid(HostGrantsRequest? request)
        => ValidRequest(request)
            && request!.ExpectedVersion >= 1
            && request.CapabilityGrants is not null
            && request.ResourceGrants is not null
            && request.CapabilityGrants.All(ValidRequest)
            && request.ResourceGrants.All(ValidRequest);

    private static bool Valid(VersionRequest? request) => ValidRequest(request) && request!.ExpectedVersion >= 1;

    private static bool Valid(HostExecutionPolicyRequest? request)
        => ValidRequest(request)
            && request!.ExpectedVersion >= 0
            && !string.IsNullOrWhiteSpace(request.Location)
            && !string.IsNullOrWhiteSpace(request.FallbackPolicy)
            && request.RequiredCapabilities is not null
            && request.RequiredResourceIds is not null
            && request.RequiredCapabilities.All(ValidRequest);

    private static bool Valid(HostPollRequest? request)
        => ValidRequest(request)
            && request!.MaxWaitSeconds is >= 1 and <= 25
            && (request.ActiveAttempt is null || ValidRequest(request.ActiveAttempt));

    private static bool Valid(HostLeaseAckRequest? request)
        => ValidRequest(request) && request!.LeaseVersion >= 1 && !string.IsNullOrWhiteSpace(request.LocalAttemptId);

    private static bool Valid(HostLeaseEventsRequest? request)
        => ValidRequest(request)
            && request!.LeaseVersion >= 1
            && !string.IsNullOrWhiteSpace(request.LocalAttemptId)
            && request.Events is not null
            && request.Events.All(ValidRequest);

    private static bool Valid(HostLeaseCompleteRequest? request)
        => ValidRequest(request)
            && request!.LeaseVersion >= 1
            && !string.IsNullOrWhiteSpace(request.LocalAttemptId)
            && !string.IsNullOrWhiteSpace(request.Outcome);

    private static bool Valid(HostLeaseReconcileRequest? request)
        => ValidRequest(request)
            && request!.LeaseVersion >= 1
            && !string.IsNullOrWhiteSpace(request.LocalAttemptId)
            && !string.IsNullOrWhiteSpace(request.ObservedState);

    private static bool Valid(HostLeaseArtifactRequest? request)
        => ValidRequest(request)
            && request!.LeaseVersion >= 1
            && !string.IsNullOrWhiteSpace(request.LocalAttemptId)
            && !string.IsNullOrWhiteSpace(request.ArtifactId)
            && !string.IsNullOrWhiteSpace(request.Kind)
            && !string.IsNullOrWhiteSpace(request.MediaType)
            && request.Summary is not null
            && request.DeclaredSize >= 0
            && !string.IsNullOrWhiteSpace(request.DeclaredSha256)
            && !string.IsNullOrWhiteSpace(request.Retention)
            && request.TextContent is not null;

    private static bool ValidRequest(StrictRequest? request)
        => request is not null && request.ExtensionData?.Count is not > 0;

    private static void NoStore(HttpContext context) => context.Response.Headers.CacheControl = "no-store";

    private static IResult Problem(int status, string code)
        => Results.Problem(statusCode: status, title: code,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    private sealed record RemoteBoundary(SqliteKernelStore? Store, string? Owner, IResult? Error);

    public abstract class StrictRequest
    {
        [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
    }

    public sealed class HostClaimRequest : StrictRequest
    {
        public string ClaimSecret { get; init; } = "";
        public JsonElement PublicKeyJwk { get; init; }
        public string Protection { get; init; } = "";
        public string Platform { get; init; } = "";
        public string Architecture { get; init; } = "";
        public string AgentVersion { get; init; } = "";
        public string ProtocolVersion { get; init; } = "";
        public CapabilityAdvertisementRequest[] RequestedCapabilities { get; init; } = [];
        public ResourceAdvertisementRequest[] RequestedResources { get; init; } = [];
    }

    public sealed class HostPairingCreateRequest : StrictRequest
    {
        public string ClaimSecretHash { get; init; } = "";
    }

    public sealed class HostConfirmRequest : StrictRequest
    {
        public long ExpectedVersion { get; init; }
        public string ConfirmationCode { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public CapabilityGrantDto[] CapabilityGrants { get; init; } = [];
        public ResourceGrantDto[] ResourceGrants { get; init; } = [];
    }

    public sealed class HostGrantsRequest : StrictRequest
    {
        public long ExpectedVersion { get; init; }
        public CapabilityGrantDto[] CapabilityGrants { get; init; } = [];
        public ResourceGrantDto[] ResourceGrants { get; init; } = [];
    }

    public sealed class VersionRequest : StrictRequest
    {
        public long ExpectedVersion { get; init; }
    }

    public sealed class HostExecutionPolicyRequest : StrictRequest
    {
        public long ExpectedVersion { get; init; }
        public string Location { get; init; } = string.Empty;
        public string? PreferredHostId { get; init; }
        public CapabilityGrantDto[] RequiredCapabilities { get; init; } = [];
        public string[] RequiredResourceIds { get; init; } = [];
        public string FallbackPolicy { get; init; } = JobExecutionFallbackPolicies.None;
    }

    public sealed class HostPollRequest : StrictRequest
    {
        public int MaxWaitSeconds { get; init; }
        public HostPollAttemptRequest? ActiveAttempt { get; init; }
    }

    public sealed class HostPollAttemptRequest : StrictRequest
    {
        public string LeaseId { get; init; } = string.Empty;
        public string LocalAttemptId { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
    }

    public sealed class HostLeaseAckRequest : StrictRequest
    {
        public long LeaseVersion { get; init; }
        public string LocalAttemptId { get; init; } = string.Empty;
        public bool Accepted { get; init; }
        public string? RejectionCode { get; init; }
    }

    public sealed class HostLeaseEventsRequest : StrictRequest
    {
        public long LeaseVersion { get; init; }
        public string LocalAttemptId { get; init; } = string.Empty;
        public HostLeaseEventRequest[] Events { get; init; } = [];
    }

    public sealed class HostLeaseEventRequest : StrictRequest
    {
        public string EventId { get; init; } = string.Empty;
        public long Sequence { get; init; }
        public string Type { get; init; } = string.Empty;
        public DateTimeOffset OccurredAt { get; init; }
        public string? Summary { get; init; }
        public JsonElement Data { get; init; }
    }

    public sealed class HostLeaseCompleteRequest : StrictRequest
    {
        public long LeaseVersion { get; init; }
        public string Outcome { get; init; } = string.Empty;
        public string? Output { get; init; }
        public string? OutputSha256 { get; init; }
        public bool Truncated { get; init; }
        public string LocalAttemptId { get; init; } = string.Empty;
    }

    public sealed class HostLeaseReconcileRequest : StrictRequest
    {
        public long LeaseVersion { get; init; }
        public string LocalAttemptId { get; init; } = string.Empty;
        public string ObservedState { get; init; } = string.Empty;
        public string? OutputSha256 { get; init; }
    }

    public sealed class HostLeaseArtifactRequest : StrictRequest
    {
        public long LeaseVersion { get; init; }
        public string? LocalAttemptId { get; init; }
        public string? ArtifactId { get; init; }
        public string? Kind { get; init; }
        public string? MediaType { get; init; }
        public string? Summary { get; init; }
        public long DeclaredSize { get; init; }
        public string? DeclaredSha256 { get; init; }
        public string? Retention { get; init; }
        public string? TextContent { get; init; }
    }

    public sealed class CapabilityAdvertisementRequest : StrictRequest
    {
        public string CapabilityId { get; init; } = "";
        public string CapabilityVersion { get; init; } = "";
        public string SchemaHash { get; init; } = "";
        public string SideEffectClass { get; init; } = "";
    }

    public sealed class ResourceAdvertisementRequest : StrictRequest
    {
        public string ResourceId { get; init; } = "";
        public string Type { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string Fingerprint { get; init; } = "";
        public string State { get; init; } = "";
    }

    public sealed class CapabilityGrantDto : StrictRequest
    {
        public string CapabilityId { get; init; } = "";
        public string CapabilityVersion { get; init; } = "";
    }

    public sealed class ResourceGrantDto : StrictRequest
    {
        public string ResourceId { get; init; } = "";
        public string AccessMode { get; init; } = "";
    }
}