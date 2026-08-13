using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using Tessera.Core.Configuration;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Tessera.Core.Stores;
using Tessera.Identity;
using Tessera.Mcp.Client;
using Tessera.Persistence.Sqlite;
using Tessera.Plugin.Abstractions;
using Tessera.Providers;

namespace Tessera.Broker;

public static class RealtimeVoiceEndpoints
{
    private const int MaximumPublicToolOutputBytes = 16 * 1024;
    private static readonly HashSet<string> Dispositions = ["COMPLETED", "INTERRUPTED", "FAILED"];
    private static readonly HashSet<string> EndReasons =
    [
        "USER_ENDED", "CONVERSATION_CHANGED", "SIGNED_OUT", "PAGE_CLOSED", "APP_BACKGROUNDED",
        "INTERRUPTED", "EXPIRED", "ERROR",
    ];

    public static void MapRealtimeVoiceEndpoints(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsPost(context.Request.Method)
                && context.Request.Path.StartsWithSegments("/api/v1/conversations")
                && context.Request.Path.Value?.Contains("/realtime-sessions", StringComparison.Ordinal) == true
                && context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
                bodyLimit.MaxRequestBodySize = 72 * 1024;
            await next(context).ConfigureAwait(false);
        });

        app.MapGet("/api/v1/realtime-voice/status", async (
            HttpContext context, ITokenValidator validator, TesseraConfig config,
            SqliteKernelStore store, RealtimeReadinessService readiness, CancellationToken token) =>
        {
            var boundary = await BoundaryAsync(context, validator, config, store, token).ConfigureAwait(false);
            if (boundary.Error is not null) return boundary.Error;
            var status = readiness.GetCached();
            return Results.Json(new
            {
                state = status.State,
                blockedCode = status.BlockedCode,
                supportsTools = status.SupportsTools,
                maxSessionSeconds = status.MaxSessionSeconds,
                checkedAt = status.CheckedAt,
                validUntil = status.ValidUntil,
                version = status.Version,
            });
        });

        app.MapPost("/api/v1/conversations/{conversationId}/realtime-sessions", async (
            HttpContext context, string conversationId, RealtimeNegotiationRequest? request,
            ITokenValidator validator, TesseraConfig config, SqliteKernelStore store,
            RealtimeVoiceService service, CancellationToken token) =>
        {
            var boundary = await BoundaryAsync(context, validator, config, store, token).ConfigureAwait(false);
            if (boundary.Error is not null) return boundary.Error;
            if (!IsJson(context)) return Problem(415, "invalid_media_type");
            if (request is null || request.ExtensionData?.Count > 0) return Problem(400, "invalid_request");
            var key = IdempotencyKey(context);
            if (key is null) return Problem(400, "invalid_idempotency_key");
            var conversation = await store.GetConversationAsync(boundary.Owner!, conversationId, token).ConfigureAwait(false);
            if (conversation is null || conversation.State != "ACTIVE") return Problem(404, "not_found");
            try
            {
                var result = await service.NegotiateAsync(boundary.Owner!, conversationId,
                    request.ClientAttemptId, key, request.OfferSdp, token).ConfigureAwait(false);
                context.Response.Headers.CacheControl = "no-store";
                return Results.Json(new
                {
                    sessionId = result.SessionId,
                    answerSdp = result.AnswerSdp,
                    negotiatedAt = result.NegotiatedAt,
                    expiresAt = result.ExpiresAt,
                    maxSessionSeconds = result.MaxSessionSeconds,
                }, statusCode: StatusCodes.Status201Created);
            }
            catch (ArgumentException) { return Problem(400, "invalid_request"); }
            catch (RealtimeProviderException exception) { return Problem(exception.StatusCode, exception.Code); }
        });

        app.MapPost("/api/v1/conversations/{conversationId}/realtime-sessions/{sessionId}/turns", async (
            HttpContext context, string conversationId, string sessionId, RealtimeTurnRequest? request,
            ITokenValidator validator, TesseraConfig config, SqliteKernelStore store, CancellationToken token) =>
        {
            var boundary = await BoundaryAsync(context, validator, config, store, token).ConfigureAwait(false);
            if (boundary.Error is not null) return boundary.Error;
            if (!IsJson(context)) return Problem(415, "invalid_media_type");
            if (request is null || request.ExtensionData?.Count > 0) return Problem(400, "invalid_request");
            var key = IdempotencyKey(context);
            if (key is null) return Problem(400, "invalid_idempotency_key");
            try
            {
                var normalizedUser = request.UserTranscript.Normalize(NormalizationForm.FormC);
                var normalizedAssistant = string.IsNullOrWhiteSpace(request.AssistantTranscript)
                    ? null : request.AssistantTranscript.Normalize(NormalizationForm.FormC);
                ValidateTurn(request, normalizedUser, normalizedAssistant);
                var session = await store.GetRealtimeSessionAsync(boundary.Owner!, sessionId, token).ConfigureAwait(false);
                if (session is null || session.ConversationId != conversationId) return Problem(404, "not_found");
                if (session.State != "NEGOTIATED" || session.ExpiresAt <= DateTimeOffset.UtcNow)
                    return Problem(409, "realtime_session_ended");

                var requestHash = RealtimeVoiceService.Hash(JsonSerializer.Serialize(new
                {
                    conversationId, sessionId, request.ClientTurnId, request.InputItemId,
                    request.OutputItemId, userTranscript = normalizedUser,
                    assistantTranscript = normalizedAssistant, request.AssistantDisposition,
                }));
                var prior = await store.GetIdempotencyReceiptAsync(boundary.Owner!, "realtime-turn", key, token).ConfigureAwait(false);
                if (prior is not null)
                {
                    if (prior.RequestHash != requestHash) return Problem(409, "idempotency_conflict");
                    var saved = await store.GetRealtimeTurnAsync(boundary.Owner!, sessionId, request.ClientTurnId, token).ConfigureAwait(false);
                    if (saved is null) return Problem(409, "realtime_turn_conflict");
                    return await TurnReceiptAsync(store, boundary.Owner!, conversationId, saved, replayed: true, token).ConfigureAwait(false);
                }
                var existing = await store.GetRealtimeTurnAsync(boundary.Owner!, sessionId, request.ClientTurnId, token).ConfigureAwait(false);
                if (existing is not null) return Problem(409, "realtime_turn_conflict");

                var now = DateTimeOffset.UtcNow;
                var userId = StableId(boundary.Owner!, sessionId, request.ClientTurnId, "user");
                var assistantId = normalizedAssistant is null ? null : StableId(boundary.Owner!, sessionId, request.ClientTurnId, "assistant");
                var user = new ChatMessage(boundary.Owner!, userId, conversationId, "USER", "PERSISTED", null,
                    [new(StableId(boundary.Owner!, sessionId, request.ClientTurnId, "user-part"), 1, "TEXT", normalizedUser)], now, now, 1);
                var assistant = assistantId is null ? null : new ChatMessage(boundary.Owner!, assistantId, conversationId,
                    "ASSISTANT", request.AssistantDisposition switch { "INTERRUPTED" => "STOPPED", "FAILED" => "FAILED", _ => "COMPLETED" }, null,
                    [new(StableId(boundary.Owner!, sessionId, request.ClientTurnId, "assistant-part"), 1, "TEXT", normalizedAssistant)], now, now, 1);
                var receipt = new RealtimeTurnReceipt(boundary.Owner!, sessionId, request.ClientTurnId,
                    request.InputItemId, request.OutputItemId, userId, assistantId, request.AssistantDisposition, now);
                var eventData = JsonSerializer.Serialize(new
                {
                    type = "realtime_turn_saved", sessionId, clientTurnId = request.ClientTurnId,
                    userMessageId = userId, assistantMessageId = assistantId,
                });
                var eventItem = new PublicExecutionEvent(boundary.Owner!, StableId(boundary.Owner!, sessionId, request.ClientTurnId, "event"),
                    sessionId, 1, "realtime_turn_saved", now, userId, null, null, eventData);
                var write = new RealtimeTurnWrite(boundary.Owner!, conversationId, sessionId, key, requestHash,
                    receipt, user, assistant, [eventItem]);
                if (!await store.SaveRealtimeTurnAsync(write, token).ConfigureAwait(false))
                    return Problem(409, "realtime_session_ended");
                return await TurnReceiptAsync(store, boundary.Owner!, conversationId, receipt, replayed: false, token).ConfigureAwait(false);
            }
            catch (ArgumentException) { return Problem(400, "invalid_request"); }
            catch (SqliteException) { return Problem(409, "realtime_turn_conflict"); }
        });

        app.MapPost("/api/v1/conversations/{conversationId}/realtime-sessions/{sessionId}/tool-calls", async (
            HttpContext context, string conversationId, string sessionId, RealtimeToolCallRequest? request,
            ITokenValidator validator, TesseraConfig config, SqliteKernelStore store,
            ICredentialStore custody, IHttpTransport transport, TesseraPluginRegistry plugins,
            IMcpClientRuntime mcpRuntime, CancellationToken token) =>
        {
            var boundary = await BoundaryAsync(context, validator, config, store, token).ConfigureAwait(false);
            if (boundary.Error is not null) return boundary.Error;
            if (!IsJson(context)) return Problem(415, "invalid_media_type");
            var key = IdempotencyKey(context);
            if (request is null || request.ExtensionData?.Count > 0 || key is null)
                return Problem(400, "invalid_request");
            try
            {
                RealtimeVoiceService.ValidateIdentifier(request.ClientCallId, nameof(request.ClientCallId));
                RealtimeVoiceService.ValidateIdentifier(request.Name, nameof(request.Name));
                if (request.Arguments.ValueKind != JsonValueKind.Object) return Problem(400, "invalid_request");
                ProductContentValidation.Json(request.Arguments, nameof(request.Arguments));
            }
            catch (ArgumentException) { return Problem(400, "invalid_request"); }
            var session = await store.GetRealtimeSessionAsync(boundary.Owner!, sessionId, token).ConfigureAwait(false);
            if (session is null || session.ConversationId != conversationId) return Problem(404, "not_found");
            if (session.State != "NEGOTIATED" || session.ExpiresAt <= DateTimeOffset.UtcNow)
                return Problem(409, "realtime_session_ended");
            var requestHash = RealtimeVoiceService.Hash(JsonSerializer.Serialize(new
            {
                conversationId,
                sessionId,
                request.ClientCallId,
                request.Name,
                arguments = request.Arguments,
            }));
            var resourceId = $"{sessionId}:{request.ClientCallId}";
            var prior = await store.GetIdempotencyReceiptAsync(
                boundary.Owner!, "realtime-tool", key, token).ConfigureAwait(false);
            if (prior is not null)
            {
                if (prior.RequestHash != requestHash || prior.ResourceId != resourceId)
                    return Problem(409, "idempotency_conflict");
                var replay = await store.GetRealtimeToolBindingAsync(
                    boundary.Owner!, sessionId, request.ClientCallId, token).ConfigureAwait(false);
                if (replay is null) return Problem(409, "realtime_tool_conflict");
                if (replay.State is "REQUESTED" or "RUNNING")
                    return Problem(409, "realtime_tool_in_progress");
                if (replay.State == "APPROVAL_REQUIRED")
                {
                    var reconciled = await ReconcileApprovedToolAsync(
                        store, boundary.Owner!, replay, prior, token).ConfigureAwait(false);
                    if (reconciled is not null) return reconciled;
                }
                return await ToolResponseAsync(store, boundary.Owner!, replay, token).ConfigureAwait(false);
            }
            if (await store.GetRealtimeToolBindingAsync(
                boundary.Owner!, sessionId, request.ClientCallId, token).ConfigureAwait(false) is not null)
                return Problem(409, "idempotency_conflict");

            var now = DateTimeOffset.UtcNow;
            var requested = new RealtimeToolBinding(boundary.Owner!, sessionId, request.ClientCallId,
                null, null, null, "REQUESTED", now, now, 1);
            var reservation = new RealtimeToolCallReservation(requested, key, requestHash);
            if (!await store.BeginRealtimeToolCallAsync(reservation, token).ConfigureAwait(false))
            {
                var racedReceipt = await store.GetIdempotencyReceiptAsync(
                    boundary.Owner!, "realtime-tool", key, token).ConfigureAwait(false);
                if (racedReceipt?.RequestHash == requestHash && racedReceipt.ResourceId == resourceId)
                    return Problem(409, "realtime_tool_in_progress");
                return Problem(409, "idempotency_conflict");
            }

            var captured = (await store.ListRealtimeSessionToolsAsync(
                boundary.Owner!, sessionId, token).ConfigureAwait(false))
                .SingleOrDefault(item => item.ExposedName == request.Name);
            if (captured is null)
                return await FailToolAsync(store, reservation, "tool_not_advertised", token).ConfigureAwait(false);
            RealtimeToolProjection current;
            try
            {
                current = await RealtimeToolProjection.ProjectAsync(
                    store, plugins, boundary.Owner!, conversationId, sessionId, token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is PluginModuleException or InvalidOperationException)
            {
                return await FailToolAsync(store, reservation, "tool_binding_changed", token).ConfigureAwait(false);
            }
            var currentBinding = current.Tools.SingleOrDefault(item => item.ExposedName == request.Name);
            if (currentBinding != captured)
                return await FailToolAsync(store, reservation, "tool_binding_changed", token).ConfigureAwait(false);
            var definition = current.Definitions.Single(item =>
                item.GetProperty("name").GetString() == request.Name);
            if (!MatchesToolSchema(request.Arguments, definition.GetProperty("parameters")))
                return await FailToolAsync(store, reservation, "invalid_tool_arguments", token).ConfigureAwait(false);

            var executionId = $"realtime:{sessionId}";
            var canonicalCallId = $"{executionId}:{request.ClientCallId}";
            try
            {
                using var callDocument = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    id = request.ClientCallId,
                    name = request.Name,
                    arguments = request.Arguments,
                }));
                var outcome = await R2ProductEndpoints.ExecuteChatToolAsync(
                    store, custody, transport, boundary.Owner!, executionId, conversationId, null,
                    current.Context, callDocument.RootElement, 1, token, plugins, mcpRuntime).ConfigureAwait(false);
                var canonical = await store.GetCapabilityReceiptAsync(
                    boundary.Owner!, canonicalCallId, token).ConfigureAwait(false);
                if (outcome.Part.Kind == "ACTION" && outcome.Part.ActionId is not null
                    && canonical is not null
                    && await store.GetActionAsync(boundary.Owner!, outcome.Part.ActionId, token).ConfigureAwait(false) is not null)
                {
                    var completed = requested with
                    {
                        CapabilityCallId = canonical.Value.Call.CallId,
                        CapabilityResultId = canonical.Value.Result?.ResultId,
                        ActionId = outcome.Part.ActionId,
                        State = "APPROVAL_REQUIRED",
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                    return await CompleteToolAsync(store, reservation, completed, 202, null, token).ConfigureAwait(false);
                }
                if (outcome.Part.Kind == "CAPABILITY_RESULT" && canonical?.Result is not null)
                {
                    if (!TryPublicToolOutput(canonical.Value.Result, out _))
                        return await FailToolAsync(store, reservation, "tool_output_unavailable", token,
                            canonical.Value.Call.CallId, canonical.Value.Result.ResultId).ConfigureAwait(false);
                    var completed = requested with
                    {
                        CapabilityCallId = canonical.Value.Call.CallId,
                        CapabilityResultId = canonical.Value.Result.ResultId,
                        State = "COMPLETED",
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                    return await CompleteToolAsync(store, reservation, completed, 200, null, token).ConfigureAwait(false);
                }
                return await FailToolAsync(store, reservation, SafeToolError(outcome.Part.ErrorCode), token,
                    canonical?.Call.CallId, canonical?.Result?.ResultId).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                or JsonException or PluginModuleException)
            {
                var canonical = await store.GetCapabilityReceiptAsync(
                    boundary.Owner!, canonicalCallId, token).ConfigureAwait(false);
                var code = exception is ArgumentException or JsonException or PluginModuleException
                    ? "invalid_tool_arguments" : "tool_failed";
                return await FailToolAsync(store, reservation, code, token,
                    canonical?.Call.CallId, canonical?.Result?.ResultId).ConfigureAwait(false);
            }
        });

        app.MapPost("/api/v1/conversations/{conversationId}/realtime-sessions/{sessionId}/end", async (
            HttpContext context, string conversationId, string sessionId, RealtimeEndRequest? request,
            ITokenValidator validator, TesseraConfig config, SqliteKernelStore store, CancellationToken token) =>
        {
            var boundary = await BoundaryAsync(context, validator, config, store, token).ConfigureAwait(false);
            if (boundary.Error is not null) return boundary.Error;
            if (!IsJson(context)) return Problem(415, "invalid_media_type");
            if (request is null || request.ExtensionData?.Count > 0 || !EndReasons.Contains(request.Reason))
                return Problem(400, "invalid_request");
            var key = IdempotencyKey(context);
            if (key is null) return Problem(400, "invalid_idempotency_key");
            var session = await store.GetRealtimeSessionAsync(boundary.Owner!, sessionId, token).ConfigureAwait(false);
            if (session is null || session.ConversationId != conversationId) return Problem(404, "not_found");
            var requestHash = RealtimeVoiceService.Hash($"{conversationId}\n{sessionId}\n{request.Reason}");
            try
            {
                var result = await store.EndRealtimeSessionAsync(boundary.Owner!, sessionId, request.Reason,
                    key, requestHash, DateTimeOffset.UtcNow, token).ConfigureAwait(false);
                return result is null ? Problem(404, "not_found") : Results.Json(new
                {
                    id = sessionId,
                    resourceType = "realtime_session",
                    version = result.Version,
                });
            }
            catch (ProductConcurrencyException) { return Problem(409, "idempotency_conflict"); }
        });
    }

    private static async Task<Boundary> BoundaryAsync(HttpContext context, ITokenValidator validator,
        TesseraConfig config, SqliteKernelStore store, CancellationToken token)
    {
        var user = await PortalEndpoints.ResolveEndUserAsync(context, validator, config).ConfigureAwait(false);
        if (user?.CanonicalPrincipalId is null || string.IsNullOrWhiteSpace(user.TenantId)) return new(null, Problem(401, "unauthenticated"));
        var principal = PrincipalRef.Create(user.Issuer, user.TenantId, user.Subject, user.PreferredUsername, DateTimeOffset.UtcNow);
        await store.AddAsync(principal, token).ConfigureAwait(false);
        return new(principal.PrincipalId, null);
    }

    private static void ValidateTurn(RealtimeTurnRequest request, string userTranscript, string? assistantTranscript)
    {
        RealtimeVoiceService.ValidateIdentifier(request.ClientTurnId, nameof(request.ClientTurnId));
        RealtimeVoiceService.ValidateIdentifier(request.InputItemId, nameof(request.InputItemId));
        if (request.OutputItemId is not null) RealtimeVoiceService.ValidateIdentifier(request.OutputItemId, nameof(request.OutputItemId));
        if (!Dispositions.Contains(request.AssistantDisposition) || string.IsNullOrWhiteSpace(userTranscript))
            throw new ArgumentException("Invalid transcript turn.");
        var userBytes = Encoding.UTF8.GetByteCount(userTranscript);
        var assistantBytes = assistantTranscript is null ? 0 : Encoding.UTF8.GetByteCount(assistantTranscript);
        if (userBytes > 32 * 1024 || assistantBytes > 32 * 1024 || userBytes + assistantBytes > 48 * 1024
            || HasInvalidTextControl(userTranscript)
            || (assistantTranscript is not null && HasInvalidTextControl(assistantTranscript)))
            throw new ArgumentException("Transcript is invalid.");
    }

    private static bool HasInvalidTextControl(string value)
        => value.Any(character => char.IsControl(character) && character is not '\n' and not '\r' and not '\t');

    private static async Task<IResult> TurnReceiptAsync(SqliteKernelStore store, string owner,
        string conversationId, RealtimeTurnReceipt receipt, bool replayed, CancellationToken token)
    {
        var messages = await store.ListMessagesAsync(owner, conversationId, token).ConfigureAwait(false);
        var user = messages.Single(item => item.MessageId == receipt.UserMessageId);
        var assistant = receipt.AssistantMessageId is null ? null : messages.Single(item => item.MessageId == receipt.AssistantMessageId);
        return Results.Json(new
        {
            sessionId = receipt.SessionId,
            clientTurnId = receipt.ClientTurnId,
            userMessage = MessageDto(user),
            assistantMessage = assistant is null ? null : MessageDto(assistant),
            replayed,
        }, statusCode: StatusCodes.Status201Created);
    }

    private static object MessageDto(ChatMessage message) => new
    {
        id = message.MessageId,
        messageId = message.MessageId,
        message.ConversationId,
        message.Role,
        message.Status,
        parts = message.Parts.Select(part => new { id = part.PartId, part.Kind, part.Text }).ToArray(),
        message.CreatedAt,
        message.CompletedAt,
        message.Version,
    };

    private static async Task<IResult> CompleteToolAsync(
        SqliteKernelStore store, RealtimeToolCallReservation reservation,
        RealtimeToolBinding completed, int status, string? errorCode, CancellationToken token)
    {
        var receiptBody = JsonSerializer.Serialize(new
        {
            completed.SessionId,
            completed.ClientCallId,
            completed.State,
            completed.CapabilityCallId,
            completed.CapabilityResultId,
            completed.ActionId,
            errorCode,
        });
        if (!await store.CompleteRealtimeToolCallAsync(
            reservation, completed, status, receiptBody, token).ConfigureAwait(false))
            return Problem(409, "realtime_tool_conflict");
        return await ToolResponseAsync(store, completed.OwnerPrincipalId, completed, token).ConfigureAwait(false);
    }

    private static Task<IResult> FailToolAsync(
        SqliteKernelStore store, RealtimeToolCallReservation reservation, string errorCode,
        CancellationToken token, string? capabilityCallId = null, string? capabilityResultId = null)
    {
        var completed = reservation.Binding with
        {
            CapabilityCallId = capabilityCallId,
            CapabilityResultId = capabilityResultId,
            State = "FAILED",
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        return CompleteToolAsync(store, reservation, completed, 200, errorCode, token);
    }

    private static async Task<IResult> ToolResponseAsync(
        SqliteKernelStore store, string owner, RealtimeToolBinding binding, CancellationToken token)
    {
        JsonElement? output = null;
        string? errorCode = null;
        if (binding.State == "COMPLETED" && binding.CapabilityCallId is not null)
        {
            var canonical = await store.GetCapabilityReceiptAsync(owner, binding.CapabilityCallId, token).ConfigureAwait(false);
            if (canonical?.Result is null || canonical.Value.Result.ResultId != binding.CapabilityResultId
                || !TryPublicToolOutput(canonical.Value.Result, out output))
            {
                output = null;
                errorCode = "tool_result_unavailable";
            }
        }
        else if (binding.State == "FAILED")
        {
            var receipt = await store.GetIdempotencyReceiptAsync(owner, "realtime-tool",
                await FindToolIdempotencyKeyAsync(store, owner, binding, token).ConfigureAwait(false), token).ConfigureAwait(false);
            if (receipt is not null)
            {
                using var document = JsonDocument.Parse(receipt.ResponseBodyJson);
                if (document.RootElement.TryGetProperty("errorCode", out var code) && code.ValueKind == JsonValueKind.String)
                    errorCode = SafeToolError(code.GetString());
            }
            errorCode ??= "tool_failed";
        }
        var status = binding.State == "APPROVAL_REQUIRED" ? StatusCodes.Status202Accepted : StatusCodes.Status200OK;
        return Results.Json(new
        {
            sessionId = binding.SessionId,
            clientCallId = binding.ClientCallId,
            state = errorCode is null ? binding.State : "FAILED",
            capabilityCallId = binding.CapabilityCallId,
            capabilityResultId = binding.CapabilityResultId,
            actionId = binding.ActionId,
            output,
            errorCode,
        }, statusCode: status);
    }

    private static async Task<IResult?> ReconcileApprovedToolAsync(
        SqliteKernelStore store, string owner, RealtimeToolBinding binding,
        ProductIdempotencyReceipt receipt, CancellationToken token)
    {
        if (binding.ActionId is null || binding.CapabilityCallId is null) return null;
        var action = await store.GetActionAsync(owner, binding.ActionId, token).ConfigureAwait(false);
        if (action is null) return null;
        if (action.State is ActionState.Proposed or ActionState.Authorized or ActionState.Started) return null;
        var reservation = new RealtimeToolCallReservation(binding, receipt.IdempotencyKey, receipt.RequestHash);
        if (action.State is ActionState.ExecutionSucceeded or ActionState.ProviderVerified or ActionState.ExternallyConfirmed)
        {
            var canonical = await store.GetCapabilityReceiptAsync(owner, binding.CapabilityCallId, token).ConfigureAwait(false);
            if (canonical?.Call.State == "SUCCEEDED" && canonical.Value.Result is not null
                && TryPublicToolOutput(canonical.Value.Result, out _))
            {
                var completed = binding with
                {
                    CapabilityResultId = canonical.Value.Result.ResultId,
                    State = "COMPLETED",
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                return await CompleteToolAsync(store, reservation, completed, 200, null, token).ConfigureAwait(false);
            }
            return await FailToolAsync(store, reservation, "tool_result_unavailable", token,
                binding.CapabilityCallId, canonical?.Result?.ResultId).ConfigureAwait(false);
        }
        var errorCode = action.State switch
        {
            ActionState.ReconciliationRequired => "provider_outcome_unknown",
            ActionState.Canceled => "action_canceled",
            ActionState.Expired => "action_expired",
            _ => SafeToolError(action.Failure),
        };
        return await FailToolAsync(store, reservation, errorCode, token, binding.CapabilityCallId).ConfigureAwait(false);
    }

    private static async Task<string> FindToolIdempotencyKeyAsync(
        SqliteKernelStore store, string owner, RealtimeToolBinding binding, CancellationToken token)
    {
        var receipt = await store.FindIdempotencyReceiptByResourceAsync(
            owner, "realtime-tool", $"{binding.SessionId}:{binding.ClientCallId}", token).ConfigureAwait(false);
        return receipt?.IdempotencyKey ?? string.Empty;
    }

    private static bool TryPublicToolOutput(ProductCapabilityResult result, out JsonElement? output)
    {
        output = null;
        if (result.Truncated || Encoding.UTF8.GetByteCount(result.DataJson) > MaximumPublicToolOutputBytes)
            return false;
        try
        {
            using var document = JsonDocument.Parse(result.DataJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            output = document.RootElement.Clone();
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static string SafeToolError(string? code) => code switch
    {
        "tool_not_advertised" or "tool_binding_changed" or "invalid_tool_arguments" or
        "tool_not_available" or "plugin_runtime_unavailable" or "account_ambiguous" or
        "account_substitution_denied" or "capability_failed" or "capability_unavailable" or
        "provider_auth_required" or "tool_output_unavailable" or "tool_result_unavailable" => code,
        "provider_outcome_unknown" or "action_canceled" or "action_expired" => code,
        _ => "tool_failed",
    };

    private static bool MatchesToolSchema(JsonElement value, JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("type", out var type)
            || type.GetString() != "object"
            || value.ValueKind != JsonValueKind.Object)
            return false;
        var properties = schema.TryGetProperty("properties", out var declared)
            && declared.ValueKind == JsonValueKind.Object ? declared : default;
        var required = schema.TryGetProperty("required", out var requiredElement)
            && requiredElement.ValueKind == JsonValueKind.Array
            ? requiredElement.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal)
            : [];
        if (required.Any(name => !value.TryGetProperty(name, out _))) return false;
        foreach (var property in value.EnumerateObject())
        {
            if (properties.ValueKind != JsonValueKind.Object
                || !properties.TryGetProperty(property.Name, out var propertySchema)
                || !MatchesSchemaValue(property.Value, propertySchema))
                return false;
        }
        return true;
    }

    private static bool MatchesSchemaValue(JsonElement value, JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) return false;
        if (schema.TryGetProperty("enum", out var allowed) && allowed.ValueKind == JsonValueKind.Array
            && !allowed.EnumerateArray().Any(item => JsonElement.DeepEquals(item, value)))
            return false;
        if (!schema.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return true;
        return type.GetString() switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number,
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => false,
        };
    }

    private static bool IsJson(HttpContext context) => context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true;
    private static string? IdempotencyKey(HttpContext context)
    {
        var value = context.Request.Headers["Idempotency-Key"].FirstOrDefault();
        try { RealtimeVoiceService.ValidateIdentifier(value ?? string.Empty, "Idempotency-Key"); return value; }
        catch (ArgumentException) { return null; }
    }
    private static string StableId(params string[] values) => RealtimeVoiceService.Hash(string.Join('\n', values));
    private static IResult Problem(int status, string code) => Results.Problem(statusCode: status, title: code,
        extensions: new Dictionary<string, object?> { ["code"] = code });

    private sealed record Boundary(string? Owner, IResult? Error);
    public sealed class RealtimeNegotiationRequest
    {
        public string ClientAttemptId { get; init; } = "";
        public string OfferSdp { get; init; } = "";
        [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
    }
    public sealed class RealtimeTurnRequest
    {
        public string ClientTurnId { get; init; } = "";
        public string InputItemId { get; init; } = "";
        public string? OutputItemId { get; init; }
        public string UserTranscript { get; init; } = "";
        public string? AssistantTranscript { get; init; }
        public string AssistantDisposition { get; init; } = "";
        [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
    }
    public sealed class RealtimeToolCallRequest
    {
        public string ClientCallId { get; init; } = "";
        public string Name { get; init; } = "";
        public JsonElement Arguments { get; init; }
        [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
    }
    public sealed class RealtimeEndRequest
    {
        public string Reason { get; init; } = "";
        [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; init; }
    }
}