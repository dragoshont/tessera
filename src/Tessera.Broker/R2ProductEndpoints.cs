using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Tessera.Core.Configuration;
using Tessera.Core.Identity;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Tessera.Core.Stores;
using Tessera.Identity;
using Tessera.Mcp.Client;
using Tessera.Persistence.Sqlite;
using Tessera.Plugin.Abstractions;
using Tessera.Providers;
using Tessera.Providers.R2;

#pragma warning disable CA2208

namespace Tessera.Broker;

internal sealed record R2CursorSigner(byte[] Key);

internal static class R2ProductEndpoints
{
    private static readonly string[] TimeZoneRequired = ["timeZone"];
    private static readonly string[] RememberRequired = ["subjectKey", "predicate", "value"];
    private static readonly string[] CorrectRequired = ["assertionId", "value"];
    private static readonly string[] AssertionRequired = ["assertionId"];
    private static readonly string[] MessageRequired = ["messageId"];

    public static void MapR2ProductEndpoints(this WebApplication app)
    {
        app.MapGet(
            "/api/v1/conversations",
            async (
                HttpContext context,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                return boundary.Error
                    ?? Page(
                        context,
                        boundary.Owner!,
                        (
                            await boundary.Store!.ListConversationsAsync(boundary.Owner!, token)
                        ).Select(ConversationDto)
                    );
            }
        );
        app.MapPost(
            "/api/v1/conversations",
            async (
                HttpContext context,
                CreateConversationRequest? request,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (request is null || !TryIdempotencyKey(context, out var key))
                    return Problem(
                        400,
                        request is null ? "invalid_request" : "invalid_idempotency_key"
                    );
                var title = string.IsNullOrWhiteSpace(request.Title)
                    ? "New conversation"
                    : ProductContentValidation.Text(request.Title, nameof(request.Title), 256);
                var id = RouteId(boundary.Owner!, "conversations", "conversation", key!);
                var existing = await boundary.Store!.GetConversationAsync(
                    boundary.Owner!,
                    id,
                    token
                );
                if (existing is not null)
                    return
                        existing.Title == title && existing.ModelProfileId == request.ModelProfileId
                        ? Results.Json(ConversationDto(existing), statusCode: 201)
                        : Problem(409, "idempotency_conflict");
                var now = DateTimeOffset.UtcNow;
                var item = new Conversation(
                    boundary.Owner!,
                    id,
                    title,
                    "ACTIVE",
                    request.ModelProfileId,
                    now,
                    now,
                    1
                );
                await boundary.Store.AddConversationAsync(item, token);
                var accounts = new List<string>();
                var capabilities = new List<(string, string)>
                {
                    ("local.time", "1"),
                    ("local.memory.remember", "1"),
                    ("local.memory.correct", "1"),
                    ("local.memory.why", "1"),
                };
                if (
                    request.ModelProfileId is not null
                    && await boundary.Store.GetModelProfileAsync(
                        boundary.Owner!,
                        request.ModelProfileId,
                        token
                    )
                        is { Enabled: true } profile
                )
                {
                    accounts.Add(profile.AccountId);
                    capabilities.Add(("model.chat.complete", "1"));
                }
                if (
                    !await boundary.Store.ReplaceConversationGrantsAsync(
                        boundary.Owner!,
                        id,
                        1,
                        accounts,
                        capabilities,
                        token
                    )
                )
                    return Problem(409, "version_conflict");
                return Results.Json(
                    ConversationDto(
                        (await boundary.Store.GetConversationAsync(boundary.Owner!, id, token))!
                    ),
                    statusCode: 201
                );
            }
        );
        app.MapGet(
            "/api/v1/conversations/{id}",
            async (
                HttpContext context,
                string id,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                var item = await boundary.Store!.GetConversationAsync(boundary.Owner!, id, token);
                return item is null
                    ? Problem(404, "not_found")
                    : Results.Json(ConversationDto(item));
            }
        );
        app.MapGet(
            "/api/v1/conversations/{id}/grants",
            async (
                HttpContext context,
                string id,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                var conversation = await boundary.Store!.GetConversationAsync(
                    boundary.Owner!,
                    id,
                    token
                );
                if (conversation is null)
                    return Problem(404, "not_found");
                var grants = await boundary.Store.GetConversationGrantsAsync(
                    boundary.Owner!,
                    id,
                    token
                );
                return Results.Json(
                    new
                    {
                        accountGrants = grants.Accounts,
                        capabilityGrants = grants
                            .Capabilities.Select(item => $"{item.Id}@{item.Version}")
                            .ToArray(),
                        conversation.Version,
                    }
                );
            }
        );
        app.MapPut(
            "/api/v1/conversations/{id}/grants",
            async (
                HttpContext context,
                string id,
                ConversationGrantsRequest? request,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (request is null)
                    return Problem(400, "invalid_request");
                foreach (var accountId in request.AccountGrants)
                    if (
                        await boundary.Store!.GetConnectedAccountAsync(
                            boundary.Owner!,
                            accountId,
                            token
                        )
                        is not { Lifecycle: AccountLifecycle.Connected }
                    )
                        return Problem(422, "account_unavailable");
                var capabilities = request
                    .CapabilityGrants.Select(item => (item.Id, item.Version))
                    .ToArray();
                return await boundary.Store!.ReplaceConversationGrantsAsync(
                    boundary.Owner!,
                    id,
                    request.ExpectedVersion,
                    request.AccountGrants,
                    capabilities,
                    token
                )
                    ? Results.Json(
                        new
                        {
                            accountGrants = request.AccountGrants,
                            capabilityGrants = request
                                .CapabilityGrants.Select(item => $"{item.Id}@{item.Version}")
                                .ToArray(),
                            version = request.ExpectedVersion + 1,
                        }
                    )
                    : Problem(409, "version_conflict");
            }
        );
        app.MapPatch(
            "/api/v1/conversations/{id}",
            async (
                HttpContext context,
                string id,
                UpdateConversationRequest? request,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (request is null)
                    return Problem(400, "invalid_request");
                if (await boundary.Store!.GetConversationAsync(boundary.Owner!, id, token) is null)
                    return Problem(404, "not_found");
                try
                {
                    var item = await boundary.Store.UpdateConversationAsync(
                        boundary.Owner!,
                        id,
                        request.Title,
                        request.State,
                        request.ExpectedVersion,
                        token
                    );
                    return item is null
                        ? Problem(409, "version_conflict")
                        : Results.Json(ConversationDto(item));
                }
                catch (ArgumentException)
                {
                    return Problem(400, "invalid_request");
                }
            }
        );
        app.MapDelete(
            "/api/v1/conversations/{id}",
            async (
                HttpContext context,
                string id,
                [FromBody] VersionRequest? request,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (request is null)
                    return Problem(400, "invalid_request");
                if (await boundary.Store!.GetConversationAsync(boundary.Owner!, id, token) is null)
                    return Problem(404, "not_found");
                return await boundary.Store.DeleteConversationAsync(
                    boundary.Owner!,
                    id,
                    request.ExpectedVersion,
                    token
                )
                    ? Results.NoContent()
                    : Problem(409, "version_conflict");
            }
        );
        app.MapPost(
            "/api/v1/conversations/{id}/retry",
            async (
                HttpContext context,
                string id,
                RetryMessageRequest? request,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                ICredentialStore custody,
                IHttpTransport transport,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (request is null)
                    return Problem(400, "invalid_request");
                if (services.GetService<R2ChatExecutionQueue>() is { } queue)
                    return await queue.RetryAsync(
                        boundary.Owner!,
                        id,
                        request.MessageId,
                        context.Request.Headers["Idempotency-Key"].FirstOrDefault(),
                        token
                    );
                return await RetryAsync(context, id, request, boundary, custody, transport, token);
            }
        );
        app.MapPost(
            "/api/v1/conversations/{id}/stop",
            async (
                HttpContext context,
                string id,
                StopExecutionRequest? request,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (request is null || !TryIdempotencyKey(context, out _))
                    return Problem(
                        400,
                        request is null ? "invalid_request" : "invalid_idempotency_key"
                    );
                if (
                    await boundary.Store!.IsExecutionStoppedAsync(
                        boundary.Owner!,
                        request.ExecutionId,
                        token
                    )
                )
                    return Results.Json(
                        new
                        {
                            resourceId = request.ExecutionId,
                            version = 2,
                            replayed = true,
                        },
                        statusCode: 202
                    );
                var version = await boundary.Store.StopExecutionAsync(
                    boundary.Owner!,
                    id,
                    request.ExecutionId,
                    1,
                    DateTimeOffset.UtcNow,
                    token
                );
                if (version is not null)
                    services.GetService<R2ChatExecutionQueue>()?.Cancel(request.ExecutionId);
                return version is null
                    ? Problem(409, "invalid_state")
                    : Results.Json(
                        new
                        {
                            resourceId = request.ExecutionId,
                            version,
                            replayed = false,
                        },
                        statusCode: 202
                    );
            }
        );
        app.MapGet(
            "/api/v1/conversations/{id}/messages",
            async (
                HttpContext context,
                string id,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (await boundary.Store!.GetConversationAsync(boundary.Owner!, id, token) is null)
                    return Problem(404, "not_found");
                return Page(
                    context,
                    boundary.Owner!,
                    (await boundary.Store.ListMessagesAsync(boundary.Owner!, id, token)).Select(
                        MessageDto
                    )
                );
            }
        );
        app.MapPost(
            "/api/v1/conversations/{id}/messages",
            async (
                HttpContext context,
                string id,
                SendMessageRequest? request,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                ICredentialStore custody,
                IHttpTransport transport,
                CancellationToken token
            ) =>
            {
                if (services.GetService<R2ChatExecutionQueue>() is { } queue)
                {
                    var queuedBoundary = await Boundary(
                        context,
                        validator,
                        config,
                        services,
                        token
                    );
                    if (queuedBoundary.Error is not null)
                        return queuedBoundary.Error;
                    if (request is null || string.IsNullOrWhiteSpace(request.Text))
                        return Problem(400, "invalid_request");
                    return await queue.AcceptAsync(
                        queuedBoundary.Owner!,
                        id,
                        request.Text,
                        request.ModelProfileId,
                        context.Request.Headers["Idempotency-Key"].FirstOrDefault(),
                        token
                    );
                }
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (request is null || string.IsNullOrWhiteSpace(request.Text))
                    return Problem(400, "invalid_request");
                string userText;
                try
                {
                    userText = ProductContentValidation.Text(
                        request.Text,
                        nameof(request.Text),
                        16384
                    );
                }
                catch (ArgumentException)
                {
                    return Problem(400, "invalid_content");
                }
                if (await boundary.Store!.GetConversationAsync(boundary.Owner!, id, token) is null)
                    return Problem(404, "not_found");
                if (string.IsNullOrWhiteSpace(request.ModelProfileId))
                    return Problem(422, "configuration_required");
                var profile = await boundary.Store.GetModelProfileAsync(
                    boundary.Owner!,
                    request.ModelProfileId,
                    token
                );
                if (profile is null || !profile.Enabled)
                    return Problem(422, "invalid_model");
                var account = await boundary.Store.GetConnectedAccountAsync(
                    boundary.Owner!,
                    profile.AccountId,
                    token
                );
                if (account is null || account.Lifecycle != AccountLifecycle.Connected)
                    return Problem(422, "configuration_required");
                var bundle = await R2ConnectedAccountService.GetValidatedBundleAsync(
                    custody,
                    account,
                    boundary.Owner!,
                    token
                );
                if (!bundle.HasAccessToken)
                    return Problem(422, "configuration_required");
                if (!TryIdempotencyKey(context, out var routeKey))
                    return Problem(400, "invalid_idempotency_key");
                var now = DateTimeOffset.UtcNow;
                var execution = RouteId(boundary.Owner!, id, "execution", routeKey!);
                var userId = RouteId(boundary.Owner!, id, "message", routeKey!);
                var existing = (
                    await boundary.Store.ListMessagesAsync(boundary.Owner!, id, token)
                ).SingleOrDefault(message => message.MessageId == userId);
                if (existing is not null)
                {
                    var prior = existing.Parts.SingleOrDefault(part => part.Kind == "TEXT")?.Text;
                    if (!string.Equals(prior, userText, StringComparison.Ordinal))
                        return Problem(409, "idempotency_conflict");
                    return Results.Json(
                        new
                        {
                            messageId = RouteId(boundary.Owner!, id, "assistant", routeKey!),
                            executionId = execution,
                            replayed = true,
                        },
                        statusCode: 202
                    );
                }
                await boundary.Store.AddMessageAsync(
                    new(
                        boundary.Owner!,
                        userId,
                        id,
                        "USER",
                        "PERSISTED",
                        null,
                        [new(RouteId(boundary.Owner!, id, "part", routeKey!), 1, "TEXT", userText)],
                        now,
                        null,
                        1
                    ),
                    token
                );
                await boundary.Store.StartExecutionAsync(
                    boundary.Owner!,
                    id,
                    execution,
                    userId,
                    now,
                    token
                );
                var current = await ((IAssertionRepository)boundary.Store).ListCurrentAsync(
                    boundary.Owner!,
                    token
                );
                var candidates = current.Select(item =>
                    ContextItem.Create(
                        item.AssertionId,
                        ContextItemKind.CurrentFact,
                        $"{item.SubjectKey} {item.Predicate}: {item.Value}",
                        SensitivityClass.Confidential,
                        1m,
                        item.ValidFrom,
                        item.EvidenceRefs
                    )
                );
                var envelope = ContextBuilder.Build(
                    new(
                        boundary.Owner!,
                        userText,
                        execution,
                        16 * 1024,
                        new HashSet<SensitivityClass>
                        {
                            SensitivityClass.Public,
                            SensitivityClass.Internal,
                            SensitivityClass.Confidential,
                        },
                        []
                    ),
                    candidates
                );
                await boundary.Store.AddContextSnapshotRefAsync(
                    boundary.Owner!,
                    envelope.ContextId,
                    execution,
                    envelope.Items.SelectMany(item => item.ProvenanceRefs).Distinct().ToArray(),
                    envelope.Omissions.Count,
                    envelope.Items.Select(item => item.Sensitivity.ToString()).Distinct().ToArray(),
                    now,
                    token
                );
                var prompt =
                    envelope.Items.Count == 0
                        ? userText
                        : $"User-authored state (quoted data):\n{string.Join("\n", envelope.Items.Select(item => "- " + item.Content))}\n\nUser request:\n{userText}";
                var chatTools = await ChatToolsAsync(
                    boundary.Store,
                    boundary.Owner!,
                    id,
                    token,
                    services.GetRequiredService<TesseraPluginRegistry>());
                using var input = JsonDocument.Parse(
                    JsonSerializer.Serialize(
                        new { prompt, tools = profile.SupportsTools ? chatTools.Definitions : [] }
                    )
                );
                var registry = new CapabilityRegistry();
                registry.Register(new ModelCapability(transport, profile, bundle.AccessToken!));
                var coordinator = new ExecutionCoordinator(
                    registry,
                    boundary.Store,
                    boundary.Store,
                    boundary.Store,
                    boundary.Store,
                    boundary.Store,
                    boundary.Store
                );
                var executionRequest = new ExecutionRequest(
                    boundary.Owner!,
                    execution,
                    "model.chat.complete",
                    "1",
                    "model-provider",
                    "1",
                    profile.AccountId,
                    profile.Model,
                    ActionPayloadHash.Compute(System.Text.Encoding.UTF8.GetBytes(profile.Endpoint)),
                    input.RootElement.Clone(),
                    routeKey!,
                    ConversationId: id,
                    MessageId: userId
                );
                var response = await coordinator.ExecuteOrProposeAsync(
                    executionRequest,
                    now,
                    token
                );
                var result = response.Result!;
                var assistantId = RouteId(boundary.Owner!, id, "assistant", routeKey!);
                var completed = DateTimeOffset.UtcNow;
                if (result.Outcome != CapabilityOutcome.Succeeded)
                {
                    var code = result.FailureCode ?? "provider_unavailable";
                    await boundary.Store.AddMessageAsync(
                        new(
                            boundary.Owner!,
                            assistantId,
                            id,
                            "ASSISTANT",
                            "FAILED",
                            null,
                            [
                                new(
                                    Guid.NewGuid().ToString("N"),
                                    1,
                                    "FAILURE",
                                    null,
                                    ErrorCode: code
                                ),
                            ],
                            now,
                            completed,
                            1
                        ),
                        token
                    );
                    await boundary.Store.AddExecutionEventAsync(
                        new(
                            boundary.Owner!,
                            Guid.NewGuid().ToString("N"),
                            execution,
                            1,
                            "failure",
                            completed,
                            assistantId,
                            null,
                            null,
                            JsonSerializer.Serialize(new { code, retryable = true })
                        ),
                        token
                    );
                    return Problem(code == "provider_timeout" ? 504 : 502, code);
                }
                var messageParts = new List<ChatMessagePart>();
                if (
                    result.Output.TryGetProperty("toolCalls", out var calls)
                    && calls.ValueKind == JsonValueKind.Array
                    && calls.GetArrayLength() > 0
                )
                {
                    if (
                        calls.GetArrayLength() > 4
                        || !result.Output.TryGetProperty(
                            "assistantMessage",
                            out var assistantMessage
                        )
                        || assistantMessage.ValueKind != JsonValueKind.Object
                    )
                        return Problem(502, "provider_malformed");
                    var outcomes = new List<ChatToolOutcome>();
                    var sequence = 1;
                    foreach (var call in calls.EnumerateArray())
                        outcomes.Add(
                            await ExecuteChatToolAsync(
                                boundary.Store,
                                custody,
                                transport,
                                boundary.Owner!,
                                execution,
                                id,
                                userId,
                                chatTools,
                                call,
                                sequence++,
                                token,
                                services.GetRequiredService<TesseraPluginRegistry>(),
                                services.GetRequiredService<IMcpClientRuntime>()
                            )
                        );
                    messageParts.AddRange(outcomes.Select(item => item.Part));
                    using var continuation = JsonDocument.Parse(
                        JsonSerializer.Serialize(
                            new
                            {
                                prompt,
                                assistantMessage,
                                toolResults = outcomes
                                    .Select(item => new
                                    {
                                        callId = item.Result.CallId,
                                        outputJson = item.Result.OutputJson,
                                    })
                                    .ToArray(),
                            }
                        )
                    );
                    var continued = await coordinator.ExecuteOrProposeAsync(
                        executionRequest with
                        {
                            ExecutionId = $"{execution}:continuation",
                            Input = continuation.RootElement.Clone(),
                            IdempotencyKey = Guid.NewGuid().ToString("N"),
                        },
                        DateTimeOffset.UtcNow,
                        token
                    );
                    result = continued.Result!;
                    if (result.Outcome != CapabilityOutcome.Succeeded)
                        return Problem(
                            result.FailureCode == "provider_timeout" ? 504 : 502,
                            result.FailureCode ?? "provider_unavailable"
                        );
                    if (
                        result.Output.TryGetProperty("toolCalls", out var repeated)
                        && repeated.ValueKind == JsonValueKind.Array
                        && repeated.GetArrayLength() > 0
                    )
                        return Problem(502, "provider_tool_loop_limit");
                }
                if (await boundary.Store.IsExecutionStoppedAsync(boundary.Owner!, execution, token))
                {
                    await boundary.Store.AddMessageAsync(
                        new(
                            boundary.Owner!,
                            assistantId,
                            id,
                            "ASSISTANT",
                            "STOPPED",
                            null,
                            [
                                new(
                                    Guid.NewGuid().ToString("N"),
                                    1,
                                    "FAILURE",
                                    null,
                                    ErrorCode: "execution_stopped"
                                ),
                            ],
                            now,
                            completed,
                            1
                        ),
                        token
                    );
                    return Results.Json(
                        new { messageId = assistantId, executionId = execution },
                        statusCode: 202
                    );
                }
                string text;
                try
                {
                    text = ProductContentValidation.Text(
                        result.Output.GetProperty("text").GetString() ?? string.Empty,
                        "modelOutput",
                        16384
                    );
                }
                catch (ArgumentException)
                {
                    var code = "provider_unsafe_content";
                    await boundary.Store.AddMessageAsync(
                        new(
                            boundary.Owner!,
                            assistantId,
                            id,
                            "ASSISTANT",
                            "FAILED",
                            null,
                            [
                                new(
                                    Guid.NewGuid().ToString("N"),
                                    1,
                                    "FAILURE",
                                    null,
                                    ErrorCode: code
                                ),
                            ],
                            now,
                            completed,
                            1
                        ),
                        token
                    );
                    return Problem(502, code);
                }
                messageParts.Add(
                    new(Guid.NewGuid().ToString("N"), messageParts.Count + 1, "TEXT", text)
                );
                await boundary.Store.AddMessageAsync(
                    new(
                        boundary.Owner!,
                        assistantId,
                        id,
                        "ASSISTANT",
                        "COMPLETED",
                        null,
                        messageParts,
                        now,
                        completed,
                        1
                    ),
                    token
                );
                await boundary.Store.CompleteExecutionAsync(
                    boundary.Owner!,
                    execution,
                    "COMPLETED",
                    completed,
                    token
                );
                await boundary.Store.AddExecutionEventAsync(
                    new(
                        boundary.Owner!,
                        Guid.NewGuid().ToString("N"),
                        execution,
                        1,
                        "completed",
                        completed,
                        assistantId,
                        null,
                        null,
                        JsonSerializer.Serialize(new { messageId = assistantId })
                    ),
                    token
                );
                return Results.Json(
                    new { messageId = assistantId, executionId = execution },
                    statusCode: 202
                );
            }
        );
        app.MapGet(
            "/api/v1/conversations/{id}/events",
            async (
                HttpContext context,
                string id,
                long? after,
                string? executionId,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (string.IsNullOrWhiteSpace(executionId))
                {
                    var snapshot = await boundary.Store!.ListConversationEventsAsync(
                        boundary.Owner!,
                        id,
                        after ?? 0,
                        token
                    );
                    return Results.Text(
                        string.Concat(
                            snapshot.Select(item =>
                                $"id: {item.Sequence}\nevent: {item.EventType}\ndata: {item.DataJson}\n\n"
                            )
                        ),
                        "text/event-stream"
                    );
                }
                if (
                    !await boundary.Store!.ExecutionBelongsToConversationAsync(
                        boundary.Owner!,
                        id,
                        executionId,
                        token
                    )
                )
                    return Problem(404, "not_found");
                context.Response.ContentType = "text/event-stream";
                context.Response.Headers.CacheControl = "no-cache";
                context.Response.Headers.Append("X-Accel-Buffering", "no");
                var cursor = after ?? 0;
                long liveCursor = 0;
                var live = services.GetService<R2LiveExecutionEvents>();
                while (!token.IsCancellationRequested)
                {
                    var wrote = false;
                    foreach (
                        var item in live?.ListAfter(boundary.Owner!, id, executionId, liveCursor)
                            ?? []
                    )
                    {
                        await context.Response.WriteAsync(
                            $"id: live-{item.Sequence}\nevent: {item.EventType}\ndata: {item.DataJson}\n\n",
                            token
                        );
                        liveCursor = item.Sequence;
                        wrote = true;
                    }
                    var events = await boundary.Store!.ListExecutionEventsAsync(
                        boundary.Owner!,
                        executionId,
                        cursor,
                        token
                    );
                    foreach (var item in events)
                    {
                        await context.Response.WriteAsync(
                            $"id: {item.Sequence}\nevent: {item.EventType}\ndata: {item.DataJson}\n\n",
                            token
                        );
                        cursor = item.Sequence;
                        wrote = true;
                    }
                    if (wrote)
                        await context.Response.Body.FlushAsync(token);
                    if (events.Any(item => item.EventType is "completed" or "failure"))
                        break;
                    await Task.Delay(50, token);
                }
                return Results.Empty;
            }
        );
        app.MapGet(
            "/api/v1/conversations/{id}/active-execution",
            async (
                HttpContext context,
                string id,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (await boundary.Store!.GetConversationAsync(boundary.Owner!, id, token) is null)
                    return Problem(404, "not_found");
                var active = (await boundary.Store.ListPendingChatExecutionsAsync(token))
                    .Where(item =>
                        item.OwnerPrincipalId == boundary.Owner && item.ConversationId == id
                    )
                    .OrderBy(item => item.ExecutionId, StringComparer.Ordinal)
                    .FirstOrDefault();
                return active is null
                    ? Results.NoContent()
                    : Results.Json(
                        new
                        {
                            active.ExecutionId,
                            active.UserMessageId,
                            active.ModelProfileId,
                        }
                    );
            }
        );
        app.MapGet(
            "/api/v1/accounts",
            async (
                HttpContext context,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                return boundary.Error
                    ?? Page(
                        context,
                        boundary.Owner!,
                        (
                            await boundary.Store!.ListConnectedAccountsAsync(boundary.Owner!, token)
                        ).Select(AccountDto)
                    );
            }
        );
        app.MapPost(
            "/api/v1/accounts",
            async (
                HttpContext context,
                CreateAccountRequest? request,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                ICredentialStore custody,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (
                    request is null
                    || string.IsNullOrWhiteSpace(request.SecretInput)
                    || custody is not ICredentialWriter writer
                )
                    return Problem(422, "invalid_configuration");
                if (!TryIdempotencyKey(context, out var key))
                    return Problem(400, "invalid_idempotency_key");
                try
                {
                    var definition = AccountDefinition(
                        request,
                        services.GetRequiredService<TesseraPluginRegistry>());
                    var accountId = RouteId(boundary.Owner!, "accounts", "account", key!);
                    var configuration = request.NonSecretConfig.GetRawText();
                    var existing = await boundary.Store!.GetConnectedAccountAsync(
                        boundary.Owner!,
                        accountId,
                        token
                    );
                    if (existing is not null)
                        return
                            existing.ProviderId == definition.ProviderId
                            && existing.PluginId == request.PluginId
                            && existing.PluginVersion == definition.PluginVersion
                            && existing.DisplayName == request.DisplayName
                            && existing.NonSecretConfigJson == configuration
                            ? Results.Json(AccountDto(existing), statusCode: 201)
                            : Problem(409, "idempotency_conflict");
                    var account = await new R2ConnectedAccountService(
                        boundary.Store,
                        writer
                    ).ConnectAsync(
                        boundary.Owner!,
                        accountId,
                        definition.ProviderId,
                        request.PluginId,
                        definition.PluginVersion,
                        request.DisplayName,
                        configuration,
                        new CredentialBundle(AccessToken: request.SecretInput),
                        definition.Permissions,
                        definition.Capabilities,
                        token
                    );
                    return Results.Json(AccountDto(account), statusCode: 201);
                }
                catch (ArgumentException)
                {
                    return Problem(422, "invalid_configuration");
                }
                catch (Exception exception)
                    when (exception
                            is R2AccountStorageException
                                or StoreException
                                or Microsoft.Data.Sqlite.SqliteException
                    )
                {
                    return Problem(503, "storage_unavailable");
                }
            }
        );
        app.MapPost(
            "/api/v1/accounts/{id}/validate",
            async (
                HttpContext context,
                string id,
                VersionRequest? request,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                ICredentialStore custody,
                IHttpTransport transport,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (request is null || !TryIdempotencyKey(context, out _))
                    return Problem(
                        400,
                        request is null ? "invalid_request" : "invalid_idempotency_key"
                    );
                var account = await boundary.Store!.GetConnectedAccountAsync(
                    boundary.Owner!,
                    id,
                    token
                );
                if (account is null)
                    return Problem(404, "not_found");
                if (
                    account.Version == request.ExpectedVersion + 1
                    && account.Health is not AccountHealth.Unknown
                )
                    return Results.Json(AccountDto(account));
                if (account.Version != request.ExpectedVersion)
                    return Problem(409, "version_conflict");
                var bundle = await R2ConnectedAccountService.GetValidatedBundleAsync(
                    custody,
                    account,
                    boundary.Owner!,
                    token
                );
                if (bundle.IsEmpty)
                    return Problem(422, "configuration_required");
                var updated = await ValidateAccountAsync(
                    boundary.Store,
                    boundary.Owner!,
                    account,
                    bundle,
                    transport,
                    services.GetRequiredService<TesseraPluginRegistry>(),
                    services.GetRequiredService<IMcpClientRuntime>(),
                    custody,
                    token
                );
                await boundary.Store.RecomputeJobsHealthAsync(boundary.Owner!, token);
                return Results.Json(AccountDto(updated));
            }
        );
        app.MapPost(
            "/api/v1/accounts/{id}/disable",
            async (
                HttpContext context,
                string id,
                VersionRequest? request,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (request is null)
                    return Problem(400, "invalid_request");
                if (
                    await boundary.Store!.GetConnectedAccountAsync(boundary.Owner!, id, token)
                    is null
                )
                    return Problem(404, "not_found");
                try
                {
                    var item = await boundary.Store.SetConnectedAccountStateAsync(
                        boundary.Owner!,
                        id,
                        request.ExpectedVersion,
                        AccountLifecycle.Disabled,
                        AccountHealth.Unknown,
                        token
                    );
                    await boundary.Store.RecomputeJobsHealthAsync(boundary.Owner!, token);
                    return Results.Json(AccountDto(item));
                }
                catch (ProductConcurrencyException)
                {
                    return Problem(409, "version_conflict");
                }
            }
        );
        app.MapDelete(
            "/api/v1/accounts/{id}",
            async (
                HttpContext context,
                string id,
                long expectedVersion,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                ICredentialStore custody,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (custody is not ICredentialWriter writer)
                    return Problem(503, "storage_unavailable");
                var account = await boundary.Store!.GetConnectedAccountAsync(
                    boundary.Owner!,
                    id,
                    token
                );
                if (account is null)
                    return Problem(404, "not_found");
                if (account.Version != expectedVersion)
                    return Problem(409, "version_conflict");
                try
                {
                    var bundle = await R2ConnectedAccountService.GetValidatedBundleAsync(
                        custody,
                        account,
                        boundary.Owner!,
                        token);
                    var pluginContext = new PluginCapabilityContext(
                        account,
                        bundle,
                        services.GetRequiredService<IHttpTransport>(),
                        services.GetRequiredService<IMcpClientRuntime>(),
                        async (reference, cancellationToken) =>
                            await custody.GetBundleAsync(reference, cancellationToken));
                    await services.GetRequiredService<TesseraPluginRegistry>()
                        .DisconnectAccountAsync(account, pluginContext, token);
                }
                catch (Exception exception)
                    when (exception is StoreException or R2AccountStorageException or UnauthorizedAccessException or PluginModuleException) { }
                return Results.Json(
                    AccountDto(
                        await new R2ConnectedAccountService(boundary.Store!, writer).RevokeAsync(
                            boundary.Owner!,
                            id,
                            expectedVersion,
                            token
                        )
                    ),
                    statusCode: 202
                );
            }
        );
        app.MapGet(
            "/api/v1/memory",
            async (
                HttpContext context,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                var includeHistory =
                    bool.TryParse(context.Request.Query["includeHistory"], out var history)
                    && history;
                var values = await boundary.Store!.ListMemoryAsync(
                    boundary.Owner!,
                    includeHistory,
                    token
                );
                var query = context.Request.Query["query"].ToString().Trim();
                if (query.Length > 200)
                    return Problem(400, "invalid_request");
                var status = context.Request.Query["status"].ToString();
                var subject = context.Request.Query["subjectKey"].ToString();
                var predicate = context.Request.Query["predicate"].ToString();
                var filtered = values.Where(item =>
                    (
                        query.Length == 0
                        || item.SubjectKey.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || item.Predicate.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || item.Value.Contains(query, StringComparison.OrdinalIgnoreCase)
                    )
                    && (
                        status.Length == 0
                        || item.EpistemicStatus.ToString()
                            .Equals(status, StringComparison.OrdinalIgnoreCase)
                    )
                    && (subject.Length == 0 || item.SubjectKey == subject)
                    && (predicate.Length == 0 || item.Predicate == predicate)
                );
                return Page(context, boundary.Owner!, filtered.Select(MemoryDto));
            }
        );
        app.MapPost(
            "/api/v1/memory",
            async (
                HttpContext context,
                MemoryRequest? request,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (request is null || !TryIdempotencyKey(context, out var key))
                    return Problem(
                        400,
                        request is null ? "invalid_request" : "invalid_idempotency_key"
                    );
                return Results.Json(
                    MemoryDto(
                        await new R2MemoryService(boundary.Store!, boundary.Store!).RememberAsync(
                            boundary.Owner!,
                            request.SubjectKey,
                            request.Predicate,
                            request.Value,
                            $"memory:{key}",
                            DateTimeOffset.UtcNow,
                            token
                        )
                    ),
                    statusCode: 201
                );
            }
        );
        app.MapPost(
            "/api/v1/memory/{id}/correct",
            async (
                HttpContext context,
                string id,
                MemoryCorrectionRequest? request,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (request is null || !TryIdempotencyKey(context, out var key))
                    return Problem(
                        400,
                        request is null ? "invalid_request" : "invalid_idempotency_key"
                    );
                return Results.Json(
                    MemoryDto(
                        await new R2MemoryService(boundary.Store!, boundary.Store!).CorrectAsync(
                            boundary.Owner!,
                            id,
                            request.Value,
                            $"memory-correction:{key}",
                            DateTimeOffset.UtcNow,
                            token
                        )
                    ),
                    statusCode: 201
                );
            }
        );
        app.MapGet(
            "/api/v1/memory/{id}/why",
            async (
                HttpContext context,
                string id,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                return Results.Json(
                    await new R2MemoryService(boundary.Store!, boundary.Store!).WhyAsync(
                        boundary.Owner!,
                        id,
                        token
                    )
                );
            }
        );
        app.MapGet(
            "/api/v1/memory/{id}/history",
            async (
                HttpContext context,
                string id,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                var item = await ((IAssertionRepository)boundary.Store!).GetAsync(
                    boundary.Owner!,
                    id,
                    token
                );
                if (item is null)
                    return Problem(404, "not_found");
                var values = await ((IAssertionRepository)boundary.Store).ListHistoryAsync(
                    boundary.Owner!,
                    item.SubjectKey,
                    item.Predicate,
                    token
                );
                return Page(
                    context,
                    boundary.Owner!,
                    values.Select(value => new
                    {
                        assertionId = value.AssertionId,
                        kind = value.EpistemicStatus.ToString(),
                        occurredAt = value.SupersededAt ?? value.CreatedAt,
                        previous = (object?)null,
                        current = MemoryDto(value),
                        evidenceRefs = value.EvidenceRefs,
                    })
                );
            }
        );
        app.MapPost(
            "/api/v1/memory/{id}/stop-using",
            async (
                HttpContext context,
                string id,
                VersionRequest? request,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (request is null || !TryIdempotencyKey(context, out _))
                    return Problem(
                        400,
                        request is null ? "invalid_request" : "invalid_idempotency_key"
                    );
                if (request.ExpectedVersion != 1)
                    return Problem(409, "version_conflict");
                var current = await ((IAssertionRepository)boundary.Store!).GetAsync(
                    boundary.Owner!,
                    id,
                    token
                );
                if (current is null)
                    return Problem(404, "not_found");
                if (current.EpistemicStatus == EpistemicStatus.Superseded)
                    return Results.Json(MemoryDto(current));
                var item = await boundary.Store.StopUsingMemoryAsync(
                    boundary.Owner!,
                    id,
                    DateTimeOffset.UtcNow,
                    token
                );
                return item is null ? Problem(409, "invalid_state") : Results.Json(MemoryDto(item));
            }
        );
        app.MapGet(
            "/api/v1/memory/follow-ups",
            async (
                HttpContext context,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                return Page(
                    context,
                    boundary.Owner!,
                    await ((IFollowUpRepository)boundary.Store!).ListFollowUpsAsync(
                        boundary.Owner!,
                        cancellationToken: token
                    )
                );
            }
        );
        app.MapGet(
            "/api/v1/plugins",
            async (
                HttpContext context,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                return boundary.Error
                    ?? Page(
                        context,
                        boundary.Owner!,
                        (
                            await boundary.Store!.ListPluginInstallationsAsync(
                                boundary.Owner!,
                                token
                            )
                        ).Select(PluginDto)
                    );
            }
        );
        app.MapPost(
            "/api/v1/plugins/{id}/versions/{version}/{operation}",
            async (
                HttpContext context,
                string id,
                string version,
                string operation,
                VersionRequest? request,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (request is null || operation is not ("enable" or "disable"))
                    return Problem(400, "invalid_request");
                if (
                    !await boundary.Store!.SetPluginEnabledAsync(
                        boundary.Owner!,
                        id,
                        version,
                        request.ExpectedVersion,
                        operation == "enable",
                        token
                    )
                )
                    return Problem(409, "version_conflict");
                await boundary.Store.RecomputeJobsHealthAsync(boundary.Owner!, token);
                return Results.Json(
                    PluginDto(
                        (
                            await boundary.Store.GetPluginInstallationAsync(
                                boundary.Owner!,
                                id,
                                version,
                                token
                            )
                        )!
                    )
                );
            }
        );
        app.MapGet(
            "/api/v1/plugins/{id}/versions/{version}",
            async (
                HttpContext context,
                string id,
                string version,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                var item = await boundary.Store!.GetPluginInstallationAsync(
                    boundary.Owner!,
                    id,
                    version,
                    token
                );
                return item is null ? Problem(404, "not_found") : Results.Json(PluginDto(item));
            }
        );
        app.MapGet(
            "/api/v1/plugins/{id}/versions/{version}/configuration",
            async (
                HttpContext context,
                string id,
                string version,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                var item = await boundary.Store!.GetPluginInstallationAsync(
                    boundary.Owner!,
                    id,
                    version,
                    token
                );
                return item is null
                    ? Problem(404, "not_found")
                    : Results.Json(
                        new
                        {
                            pluginId = id,
                            pluginVersion = version,
                            values = JsonDocument.Parse(item.ConfigurationJson).RootElement.Clone(),
                            configured = item.ConfigurationJson != "{}",
                            version = item.Version,
                        }
                    );
            }
        );
        app.MapPut(
            "/api/v1/plugins/{id}/versions/{version}/configuration",
            async (
                HttpContext context,
                string id,
                string version,
                PluginConfigurationRequest? request,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (request is null)
                    return Problem(400, "invalid_request");
                try
                {
                    var item = await boundary.Store!.SetPluginConfigurationAsync(
                        boundary.Owner!,
                        id,
                        version,
                        request.ExpectedVersion,
                        request.Values.GetRawText(),
                        token
                    );
                    return item is null
                        ? Problem(409, "version_conflict")
                        : Results.Json(
                            new
                            {
                                pluginId = id,
                                pluginVersion = version,
                                values = request.Values,
                                configured = true,
                                version = item.Version,
                            }
                        );
                }
                catch (Exception exception) when (exception is ArgumentException or JsonException)
                {
                    return Problem(400, "invalid_configuration");
                }
            }
        );
        app.MapDelete(
            "/api/v1/plugins/{id}/versions/{version}",
            async (
                HttpContext context,
                string id,
                string version,
                [FromBody] VersionRequest? request,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (request is null)
                    return Problem(400, "invalid_request");
                if (
                    await boundary.Store!.GetPluginInstallationAsync(
                        boundary.Owner!,
                        id,
                        version,
                        token
                    )
                    is null
                )
                    return Problem(404, "not_found");
                var error = await boundary.Store.RemovePluginAsync(
                    boundary.Owner!,
                    id,
                    version,
                    request.ExpectedVersion,
                    token
                );
                return error is null ? Results.NoContent() : Problem(409, error);
            }
        );
        app.MapGet(
            "/api/v1/capabilities",
            async (
                HttpContext context,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                var values = new List<object>();
                foreach (
                    var plugin in await boundary.Store!.ListPluginInstallationsAsync(
                        boundary.Owner!,
                        token
                    )
                )
                {
                    PluginManifest? manifest;
                    try
                    {
                        manifest = JsonSerializer.Deserialize<PluginManifest>(plugin.ManifestJson);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }
                    if (manifest?.Capabilities is null)
                        continue;
                    foreach (var capability in manifest.Capabilities)
                    {
                        var available =
                            plugin.Enabled
                            && (
                                !capability.AccountRequired
                                || (
                                    await boundary.Store.ListConnectedAccountsAsync(
                                        boundary.Owner!,
                                        token
                                    )
                                ).Any(account =>
                                    account.Lifecycle == AccountLifecycle.Connected
                                    && capability.RequiredPermissions.All(permission =>
                                        account.Permissions.Contains(
                                            permission,
                                            StringComparer.Ordinal
                                        )
                                    )
                                    && account.CapabilityBindings.Any(binding =>
                                        binding.PluginId == plugin.PluginId
                                        && binding.PluginVersion == plugin.PluginVersion
                                        && binding.CapabilityId == capability.Id
                                        && binding.CapabilityVersion == capability.Version
                                    )
                                )
                            );
                        values.Add(
                            new
                            {
                                id = capability.Id,
                                version = capability.Version,
                                pluginId = plugin.PluginId,
                                capability.Description,
                                capability.AccountRequired,
                                capability.RequiredPermissions,
                                capability.SideEffectClass,
                                available,
                                blockedCode = available ? null
                                : plugin.Enabled ? "account_unavailable"
                                : "plugin_disabled",
                            }
                        );
                    }
                }
                return Page(context, boundary.Owner!, values);
            }
        );
        app.MapPost(
            "/api/v1/capabilities/{id}/invoke",
            async (
                HttpContext context,
                string id,
                ReadCapabilityRequest? request,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                ICredentialStore custody,
                IHttpTransport transport,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (request is null || request.CapabilityId != id)
                    return Problem(400, "invalid_request");
                var idempotency = context.Request.Headers["Idempotency-Key"].FirstOrDefault();
                if (string.IsNullOrWhiteSpace(idempotency))
                    return Problem(400, "invalid_idempotency_key");
                try
                {
                    var store = boundary.Store!;
                    if (
                        request.ConversationId is not null
                        && await store.GetConversationAsync(
                            boundary.Owner!,
                            request.ConversationId,
                            token
                        )
                            is null
                    )
                        return Problem(404, "not_found");
                    var execution = Guid.NewGuid().ToString("N");
                    var executionRequest = new ExecutionRequest(
                        boundary.Owner!,
                        execution,
                        request.CapabilityId,
                        request.CapabilityVersion,
                        request.PluginId,
                        request.PluginVersion,
                        request.AccountId,
                        request.Target,
                        ActionPayloadHash.Compute(Encoding.UTF8.GetBytes(request.Target)),
                        request.Input.Clone(),
                        idempotency,
                        request.ConversationId,
                        request.MessageId
                    );
                    var registry = await ReadRegistry(
                        store,
                        custody,
                        transport,
                        executionRequest,
                        token,
                        services.GetRequiredService<TesseraPluginRegistry>(),
                        services.GetRequiredService<IMcpClientRuntime>()
                    );
                    var coordinator = new ExecutionCoordinator(
                        registry,
                        store,
                        store,
                        store,
                        store,
                        store
                    );
                    var response = await coordinator.ExecuteOrProposeAsync(
                        executionRequest,
                        DateTimeOffset.UtcNow,
                        token
                    );
                    if (response.ApprovalRequired || response.Result is null)
                        return Problem(409, "invalid_state");
                    if (response.Result.Outcome != CapabilityOutcome.Succeeded)
                        return Problem(
                            response.Result.FailureCode == "provider_timeout" ? 504 : 502,
                            response.Result.FailureCode ?? "provider_unavailable"
                        );
                    var now = DateTimeOffset.UtcNow;
                    var output = response.Result.Output.GetRawText();
                    var excerpt = output.Length <= 4096 ? output : output[..4096];
                    var producer = ProducerRef.Create(
                        $"plugin:{request.PluginId}",
                        request.PluginVersion
                    );
                    var evidence = EvidenceRecord.Create(
                        $"evidence:capability:{execution}",
                        boundary.Owner!,
                        "capability.result",
                        execution,
                        $"tessera://capability/{request.CapabilityId}/{execution}",
                        now,
                        now,
                        "sha256",
                        1,
                        ActionPayloadHash.Compute(Encoding.UTF8.GetBytes(output)),
                        RetentionState.Active,
                        SensitivityClass.Internal,
                        producer,
                        1,
                        excerpt
                    );
                    await ((IEvidenceRepository)store).AddAsync(boundary.Owner!, evidence, token);
                    var observation = ObservationEvent.Create(
                        $"event:capability:{execution}",
                        boundary.Owner!,
                        "CapabilityCompleted",
                        now,
                        now,
                        [boundary.Owner!],
                        [execution],
                        [evidence.EvidenceId],
                        new Dictionary<string, string>
                        {
                            { "capabilityId", request.CapabilityId },
                            { "pluginId", request.PluginId },
                        },
                        producer,
                        1
                    );
                    await ((IEventRepository)store).AppendAsync(
                        boundary.Owner!,
                        observation,
                        token
                    );
                    if (request.ConversationId is not null)
                    {
                        var messageId = Guid.NewGuid().ToString("N");
                        await store.AddMessageAsync(
                            new(
                                boundary.Owner!,
                                messageId,
                                request.ConversationId,
                                "CAPABILITY",
                                "COMPLETED",
                                null,
                                [
                                    new(
                                        Guid.NewGuid().ToString("N"),
                                        1,
                                        "CAPABILITY_RESULT",
                                        excerpt,
                                        CapabilityResultId: execution,
                                        EvidenceRefs: [evidence.EvidenceId]
                                    ),
                                ],
                                now,
                                now,
                                1
                            ),
                            token
                        );
                    }
                    return Results.Json(
                        new
                        {
                            executionId = execution,
                            result = response.Result.Output,
                            evidenceRefs = new[] { evidence.EvidenceId },
                        }
                    );
                }
                catch (KeyNotFoundException)
                {
                    return Problem(404, "not_found");
                }
                catch (InvalidOperationException exception)
                {
                    return Problem(422, exception.Message);
                }
                catch (ArgumentException)
                {
                    return Problem(400, "invalid_request");
                }
            }
        );
        app.MapGet(
            "/api/v1/jobs",
            async (
                HttpContext context,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                var jobs = await boundary.Store!.ListJobsAsync(boundary.Owner!, token);
                var values = await Task.WhenAll(
                    jobs.Select(async job =>
                    {
                        var runs = await boundary.Store.ListJobRunsAsync(
                            boundary.Owner!,
                            job.JobId,
                            token
                        );
                        return JobDto(job, runs.Count > 0 ? runs[0] : null);
                    })
                );
                return Page(context, boundary.Owner!, values);
            }
        );
        app.MapGet(
            "/api/v1/jobs/{id}",
            async (
                HttpContext context,
                string id,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                var job = await boundary.Store!.GetJobAsync(boundary.Owner!, id, token);
                if (job is null)
                    return Problem(404, "not_found");
                var runs = await boundary.Store.ListJobRunsAsync(boundary.Owner!, id, token);
                return Results.Json(JobDto(job, runs.Count > 0 ? runs[0] : null));
            }
        );
        app.MapPost(
            "/api/v1/jobs",
            async (
                HttpContext context,
                CreateJobRequest? request,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (request is null || !TryIdempotencyKey(context, out var key))
                    return Problem(
                        400,
                        request is null ? "invalid_request" : "invalid_idempotency_key"
                    );
                var profiles = await boundary.Store!.ListModelProfilesAsync(boundary.Owner!, token);
                var profile = request.ModelProfileId is null
                    ? profiles.SingleOrDefault(item => item.Enabled)
                    : profiles.SingleOrDefault(item =>
                        item.ProfileId == request.ModelProfileId && item.Enabled
                    );
                if (profile is null)
                    return Problem(422, "configuration_required");
                var name = ProductContentValidation.Text(request.Name, nameof(request.Name), 256);
                var instruction = ProductContentValidation.Text(
                    request.Instruction,
                    nameof(request.Instruction),
                    8192
                );
                var accounts = (
                    request.AccountGrants is { Length: > 0 }
                        ? request.AccountGrants
                        : [profile.AccountId]
                )
                    .Distinct(StringComparer.Ordinal)
                    .Order()
                    .ToArray();
                var capabilities = (
                    request.CapabilityGrants is { Length: > 0 }
                        ? request.CapabilityGrants.Select(item => (item.Id, item.Version))
                        : [("model.chat.complete", "1")]
                )
                    .Distinct()
                    .OrderBy(item => item.Id)
                    .ThenBy(item => item.Version)
                    .ToArray();
                var effects = (request.SideEffectGrants ?? [])
                    .Distinct(StringComparer.Ordinal)
                    .Order()
                    .ToArray();
                var contextJson = JsonSerializer.Serialize(request.ContextPolicy);
                var id = RouteId(boundary.Owner!, "jobs", "job", key!);
                var existing = await boundary.Store.GetJobAsync(boundary.Owner!, id, token);
                if (existing is not null)
                    return JobRequestMatches(
                        existing,
                        name,
                        instruction,
                        request.DesiredState,
                        profile.ProfileId,
                        request.Schedule,
                        contextJson,
                        accounts,
                        capabilities,
                        effects
                    )
                        ? Results.Json(JobDto(existing), statusCode: 201)
                        : Problem(409, "idempotency_conflict");
                var now = DateTimeOffset.UtcNow;
                var next = JobScheduleCalculator.Next(request.Schedule, now.AddTicks(-1));
                var job = new ProductJob(
                    boundary.Owner!,
                    id,
                    name,
                    instruction,
                    request.DesiredState,
                    "READY",
                    profile.ProfileId,
                    request.Schedule,
                    next,
                    contextJson,
                    accounts,
                    capabilities,
                    effects,
                    now,
                    now,
                    1
                );
                await boundary.Store.AddJobAsync(job, token);
                return Results.Json(JobDto(job), statusCode: 201);
            }
        );
        app.MapPost(
            "/api/v1/jobs/{id}/run",
            async (
                HttpContext context,
                string id,
                VersionRequest? request,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (request is null || !TryIdempotencyKey(context, out var key))
                    return Problem(
                        400,
                        request is null ? "invalid_request" : "invalid_idempotency_key"
                    );
                var runId = RouteId(boundary.Owner!, id, "manual-run", key!);
                var run = await boundary.Store!.CreateManualRunAsync(
                    boundary.Owner!,
                    id,
                    runId,
                    request.ExpectedVersion,
                    DateTimeOffset.UtcNow,
                    token
                );
                return run is null
                    ? Problem(409, "version_or_idempotency_conflict")
                    : Results.Json(JobRunDto(run), statusCode: 202);
            }
        );
        app.MapPost(
            "/api/v1/jobs/{id}/{operation}",
            async (
                HttpContext context,
                string id,
                string operation,
                VersionRequest? request,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (request is null || operation is not ("pause" or "resume"))
                    return Problem(400, "invalid_request");
                if (
                    !await boundary.Store!.SetJobDesiredStateAsync(
                        boundary.Owner!,
                        id,
                        request.ExpectedVersion,
                        operation == "pause" ? "PAUSED" : "ACTIVE",
                        token
                    )
                )
                    return Problem(409, "version_conflict");
                return Results.Json(
                    JobDto((await boundary.Store.GetJobAsync(boundary.Owner!, id, token))!)
                );
            }
        );
        app.MapPatch(
            "/api/v1/jobs/{id}",
            async (
                HttpContext context,
                string id,
                UpdateJobRequest? request,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (request is null)
                    return Problem(400, "invalid_request");
                var current = await boundary.Store!.GetJobAsync(boundary.Owner!, id, token);
                if (current is null)
                    return Problem(404, "not_found");
                var schedule = request.Schedule ?? current.Schedule;
                var updated = current with
                {
                    Name = request.Name ?? current.Name,
                    Instruction = request.Instruction ?? current.Instruction,
                    DesiredState = request.DesiredState ?? current.DesiredState,
                    ModelProfileId = request.ModelProfileId ?? current.ModelProfileId,
                    Schedule = schedule,
                    NextOccurrence = request.Schedule is null
                        ? current.NextOccurrence
                        : JobScheduleCalculator.Next(schedule, DateTimeOffset.UtcNow.AddTicks(-1)),
                    ContextPolicyJson =
                        request.ContextPolicy?.GetRawText() ?? current.ContextPolicyJson,
                    AccountGrants = request.AccountGrants ?? current.AccountGrants,
                    CapabilityGrants =
                        request.CapabilityGrants?.Select(item => (item.Id, item.Version)).ToArray()
                        ?? current.CapabilityGrants,
                    SideEffectGrants = request.SideEffectGrants ?? current.SideEffectGrants,
                };
                var item = await boundary.Store.UpdateJobAsync(
                    updated,
                    request.ExpectedVersion,
                    token
                );
                return item is null ? Problem(409, "version_conflict") : Results.Json(JobDto(item));
            }
        );
        app.MapDelete(
            "/api/v1/jobs/{id}",
            async (
                HttpContext context,
                string id,
                [FromBody] VersionRequest? request,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (request is null)
                    return Problem(400, "invalid_request");
                if (await boundary.Store!.GetJobAsync(boundary.Owner!, id, token) is null)
                    return Problem(404, "not_found");
                if (
                    !await boundary.Store.SetJobDesiredStateAsync(
                        boundary.Owner!,
                        id,
                        request.ExpectedVersion,
                        "CANCELED",
                        token
                    )
                )
                    return Problem(409, "version_conflict");
                return Results.Json(
                    JobDto((await boundary.Store.GetJobAsync(boundary.Owner!, id, token))!),
                    statusCode: 202
                );
            }
        );
        app.MapGet(
            "/api/v1/jobs/{id}/runs",
            async (
                HttpContext context,
                string id,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (await boundary.Store!.GetJobAsync(boundary.Owner!, id, token) is null)
                    return Problem(404, "not_found");
                return Page(
                    context,
                    boundary.Owner!,
                    (await boundary.Store.ListJobRunsAsync(boundary.Owner!, id, token)).Select(
                        item => JobRunDto(item)
                    )
                );
            }
        );
        app.MapGet(
            "/api/v1/job-runs/{id}",
            async (
                HttpContext context,
                string id,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                var run = await boundary.Store!.GetJobRunAsync(boundary.Owner!, id, token);
                if (run is null)
                    return Problem(404, "not_found");
                return Results.Json(
                    await JobRunDetail(boundary.Store, boundary.Owner!, run, token)
                );
            }
        );
        app.MapGet(
            "/api/v1/job-runs/{id}/actions",
            async (
                HttpContext context,
                string id,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (await boundary.Store!.GetJobRunAsync(boundary.Owner!, id, token) is null)
                    return Problem(404, "not_found");
                var actions = await ActionsForRun(boundary.Store, boundary.Owner!, id, token);
                return Page(
                    context,
                    boundary.Owner!,
                    await Task.WhenAll(
                        actions.Select(item =>
                            ActionDto(boundary.Store, boundary.Owner!, item, token)
                        )
                    )
                );
            }
        );
        app.MapGet(
            "/api/v1/job-runs/{id}/outputs",
            async (
                HttpContext context,
                string id,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (await boundary.Store!.GetJobRunAsync(boundary.Owner!, id, token) is null)
                    return Problem(404, "not_found");
                return Page(
                    context,
                    boundary.Owner!,
                    await boundary.Store.ListJobRunOutputsAsync(boundary.Owner!, id, token)
                );
            }
        );
        app.MapGet(
            "/api/v1/job-runs/{id}/capability-uses",
            async (
                HttpContext context,
                string id,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (await boundary.Store!.GetJobRunAsync(boundary.Owner!, id, token) is null)
                    return Problem(404, "not_found");
                return Page(
                    context,
                    boundary.Owner!,
                    (
                        await boundary.Store.ListCapabilityCallsAsync(boundary.Owner!, id, token)
                    ).Select(CapabilityCallDto)
                );
            }
        );
        app.MapGet(
            "/api/v1/job-runs/{id}/account-uses",
            async (
                HttpContext context,
                string id,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (await boundary.Store!.GetJobRunAsync(boundary.Owner!, id, token) is null)
                    return Problem(404, "not_found");
                return Page(
                    context,
                    boundary.Owner!,
                    (await boundary.Store.ListCapabilityCallsAsync(boundary.Owner!, id, token))
                        .Where(call => call.AccountId is not null)
                        .Select(call => new
                        {
                            callId = call.CallId,
                            accountId = call.AccountId,
                            call.CapabilityId,
                            call.State,
                            call.CreatedAt,
                        })
                        .ToArray()
                );
            }
        );
        app.MapGet(
            "/api/v1/job-runs/{id}/evidence",
            async (
                HttpContext context,
                string id,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (await boundary.Store!.GetJobRunAsync(boundary.Owner!, id, token) is null)
                    return Problem(404, "not_found");
                var refs = (
                    await boundary.Store.ListCapabilityResultsAsync(boundary.Owner!, id, token)
                )
                    .SelectMany(result => result.EvidenceRefs)
                    .Distinct()
                    .ToArray();
                var values = new List<EvidenceRecord>();
                foreach (var reference in refs)
                    if (
                        await ((IEvidenceRepository)boundary.Store).GetAsync(
                            boundary.Owner!,
                            reference,
                            token
                        ) is
                        { } evidence
                    )
                        values.Add(evidence);
                return Page(context, boundary.Owner!, values);
            }
        );
        app.MapGet(
            "/api/v1/job-runs/{id}/trace",
            async (
                HttpContext context,
                string id,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (await boundary.Store!.GetJobRunAsync(boundary.Owner!, id, token) is null)
                    return Problem(404, "not_found");
                var values = (
                    await boundary.Store.ListJobRunCheckpointsAsync(boundary.Owner!, id, token)
                ).Select(item => new
                {
                    sequence = item.Sequence,
                    occurredAt = item.CreatedAt,
                    type = item.Step,
                    summary = item.Step,
                    capabilityCallId = (string?)null,
                    actionId = CheckpointActionId(item.StateJson),
                    approvalState = (string?)null,
                    verificationState = (string?)null,
                    outputRef = (string?)null,
                    evidenceRefs = Array.Empty<string>(),
                    errorCode = (string?)null,
                });
                return Page(context, boundary.Owner!, values);
            }
        );
        app.MapGet(
            "/api/v1/settings/model-profiles",
            async (
                HttpContext context,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                return boundary.Error
                    ?? Page(
                        context,
                        boundary.Owner!,
                        await boundary.Store!.ListModelProfilesAsync(boundary.Owner!, token)
                    );
            }
        );
        app.MapPost(
            "/api/v1/settings/model-profiles",
            async (
                HttpContext context,
                CreateModelProfileRequest? request,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (
                    request is null
                    || request.AdapterKind
                        is not ("openai-compatible-local" or "openai-compatible-remote")
                    || !TryIdempotencyKey(context, out var key)
                )
                    return Problem(
                        400,
                        request is null
                        || request.AdapterKind
                            is not ("openai-compatible-local" or "openai-compatible-remote")
                            ? "invalid_request"
                            : "invalid_idempotency_key"
                    );
                string model;
                try
                {
                    model = ProductContentValidation.Text(
                        request.Model,
                        nameof(request.Model),
                        256
                    );
                    if (request.ContextLimit is < 256 or > 2_000_000)
                        throw new ArgumentOutOfRangeException(nameof(request.ContextLimit));
                }
                catch (ArgumentException)
                {
                    return Problem(400, "invalid_request");
                }
                var account = await boundary.Store!.GetConnectedAccountAsync(
                    boundary.Owner!,
                    request.AccountId,
                    token
                );
                if (
                    account?.Lifecycle != AccountLifecycle.Connected
                    || account.ProviderId != "openai-compatible"
                    || account.PluginId != "model-provider"
                )
                    return Problem(422, "configuration_required");
                string endpoint;
                try
                {
                    using var configuration = JsonDocument.Parse(account.NonSecretConfigJson);
                    endpoint =
                        configuration.RootElement.GetProperty("endpoint").GetString()
                        ?? string.Empty;
                }
                catch (Exception exception)
                    when (exception
                            is JsonException
                                or KeyNotFoundException
                                or InvalidOperationException
                    )
                {
                    return Problem(422, "invalid_configuration");
                }
                if (
                    !string.Equals(
                        endpoint.TrimEnd('/'),
                        request.Endpoint.TrimEnd('/'),
                        StringComparison.Ordinal
                    )
                )
                    return Problem(422, "invalid_configuration");
                var local =
                    endpoint.StartsWith("http://127.0.0.1", StringComparison.Ordinal)
                    || endpoint.StartsWith("http://localhost", StringComparison.Ordinal);
                if (local != (request.AdapterKind == "openai-compatible-local"))
                    return Problem(422, "invalid_configuration");
                var profileId = RouteId(boundary.Owner!, "model-profiles", "profile", key!);
                var existing = await boundary.Store.GetModelProfileAsync(
                    boundary.Owner!,
                    profileId,
                    token
                );
                if (existing is not null)
                    return
                        existing.AccountId == request.AccountId
                        && existing.AdapterKind == request.AdapterKind
                        && existing.Endpoint == endpoint
                        && existing.Model == model
                        && existing.ContextLimit == request.ContextLimit
                        ? Results.Json(existing, statusCode: 201)
                        : Problem(409, "idempotency_conflict");
                var now = DateTimeOffset.UtcNow;
                var profile = new ModelProfile(
                    boundary.Owner!,
                    profileId,
                    request.AccountId,
                    request.AdapterKind,
                    endpoint,
                    model,
                    request.ContextLimit,
                    true,
                    true,
                    true,
                    now,
                    now,
                    1
                );
                await boundary.Store.AddModelProfileAsync(profile, token);
                return Results.Json(profile, statusCode: 201);
            }
        );
        app.MapGet(
            "/api/v1/actions",
            async (
                HttpContext context,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                return await ActionList(context, boundary.Store!, boundary.Owner!, token);
            }
        );
        app.MapPost(
            "/api/v1/actions",
            async (
                HttpContext context,
                ActionProposalRequest? request,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                ICredentialStore custody,
                IHttpTransport transport,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (request is null)
                    return Problem(400, "invalid_request");
                var store = boundary.Store!;
                var key = context.Request.Headers["Idempotency-Key"].FirstOrDefault();
                if (string.IsNullOrWhiteSpace(key))
                    return Problem(400, "invalid_idempotency_key");
                var now = DateTimeOffset.UtcNow;
                var executionId = request.JobRunId ?? Guid.NewGuid().ToString("N");
                var executionRequest = new ExecutionRequest(
                    boundary.Owner!,
                    executionId,
                    request.CapabilityId,
                    request.CapabilityVersion,
                    request.PluginId,
                    request.PluginVersion,
                    request.AccountId,
                    request.Target,
                    ActionPayloadHash.Compute(System.Text.Encoding.UTF8.GetBytes(request.Target)),
                    request.Input.Clone(),
                    key,
                    request.ConversationId,
                    request.MessageId,
                    request.JobId,
                    request.JobRunId
                );
                try
                {
                    var registry = await ApprovalRegistry(
                        store,
                        custody,
                        transport,
                        executionRequest,
                        token,
                        services.GetRequiredService<TesseraPluginRegistry>(),
                        services.GetRequiredService<IMcpClientRuntime>()
                    );
                    long? fence = null;
                    ProductJobRun? run = null;
                    if (request.JobRunId is not null)
                    {
                        if (request.JobId is null)
                            return Problem(400, "invalid_request");
                        run = await store.GetJobRunAsync(boundary.Owner!, request.JobRunId, token);
                        if (run is null || run.JobId != request.JobId)
                            return Problem(404, "not_found");
                        fence = await store.AcquireRunLeaseAsync(
                            boundary.Owner!,
                            run.RunId,
                            Environment.MachineName,
                            now,
                            TimeSpan.FromMinutes(2),
                            token
                        );
                        if (
                            fence is null
                            || run.State != "QUEUED"
                            || !await store.StartRunAsync(
                                boundary.Owner!,
                                run.RunId,
                                run.Version,
                                fence.Value,
                                now,
                                token
                            )
                        )
                            return Problem(409, "invalid_state");
                    }
                    var coordinator = new ExecutionCoordinator(
                        registry,
                        store,
                        store,
                        store,
                        store,
                        store
                    );
                    var response = await coordinator.ExecuteOrProposeAsync(
                        executionRequest,
                        now,
                        token
                    );
                    if (!response.ApprovalRequired || response.Action is null)
                        return Problem(409, "invalid_state");
                    if (
                        run is not null
                        && !await store.WaitForRunApprovalAsync(
                            boundary.Owner!,
                            run.RunId,
                            fence!.Value,
                            response.Action.ActionId,
                            executionRequest,
                            now,
                            token
                        )
                    )
                        return Problem(409, "version_conflict");
                    return Results.Json(
                        await ActionDto(store, boundary.Owner!, response.Action, token),
                        statusCode: 201
                    );
                }
                catch (InvalidOperationException exception)
                {
                    return Problem(422, exception.Message);
                }
            }
        );
        app.MapGet(
            "/api/v1/actions/{id}",
            async (
                HttpContext context,
                string id,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                var item = await boundary.Store!.GetActionAsync(boundary.Owner!, id, token);
                return item is null
                    ? Problem(404, "not_found")
                    : Results.Json(await ActionDto(boundary.Store, boundary.Owner!, item, token));
            }
        );
        app.MapPost(
            "/api/v1/actions/{id}/approve",
            async (
                HttpContext context,
                string id,
                ApprovalRequest? request,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                ICredentialStore custody,
                IHttpTransport transport,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (request is null || request.ExtensionData is { Count: > 0 })
                    return Problem(400, "invalid_request");
                var key = context.Request.Headers["Idempotency-Key"].FirstOrDefault();
                if (string.IsNullOrWhiteSpace(key))
                    return Problem(400, "invalid_idempotency_key");
                try
                {
                    var durable = await (
                        (IDurableExecutionRequestRepository)boundary.Store!
                    ).GetAsync(boundary.Owner!, id, token);
                    if (durable is null)
                        return Problem(404, "not_found");
                    var registry = await ApprovalRegistry(
                        boundary.Store,
                        custody,
                        transport,
                        durable,
                        token,
                        services.GetRequiredService<TesseraPluginRegistry>(),
                        services.GetRequiredService<IMcpClientRuntime>()
                    );
                    var coordinator = new ExecutionCoordinator(
                        registry,
                        boundary.Store,
                        boundary.Store,
                        boundary.Store,
                        boundary.Store,
                        boundary.Store
                    );
                    var response = await coordinator.ApproveAndExecuteAsync(
                        boundary.Owner!,
                        id,
                        request.ExpectedVersion,
                        key,
                        DateTimeOffset.UtcNow,
                        token
                    );
                    return Results.Json(
                        await ActionDto(boundary.Store, boundary.Owner!, response.Action!, token),
                        statusCode: 202
                    );
                }
                catch (ProductConcurrencyException)
                {
                    return Problem(409, "version_conflict");
                }
                catch (UnauthorizedAccessException)
                {
                    return Problem(409, "invalid_state");
                }
                catch (InvalidOperationException exception)
                {
                    return Problem(422, exception.Message);
                }
            }
        );
        app.MapPost(
            "/api/v1/actions/{id}/cancel",
            async (
                HttpContext context,
                string id,
                VersionRequest? request,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (request is null)
                    return Problem(400, "invalid_request");
                var item = await boundary.Store!.CancelActionAsync(
                    boundary.Owner!,
                    id,
                    request.ExpectedVersion,
                    DateTimeOffset.UtcNow,
                    token
                );
                return item is null
                    ? Problem(409, "invalid_state")
                    : Results.Json(await ActionDto(boundary.Store, boundary.Owner!, item, token));
            }
        );
        app.MapGet(
            "/api/v1/settings",
            async (
                HttpContext context,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                return boundary.Error
                    ?? Results.Json(
                        SettingsDto(await boundary.Store!.GetSettingsAsync(boundary.Owner!, token))
                    );
            }
        );
        app.MapPatch(
            "/api/v1/settings",
            async (
                HttpContext context,
                UpdateSettingsRequest? request,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                if (request is null)
                    return Problem(400, "invalid_request");
                try
                {
                    var item = await boundary.Store!.UpdateSettingsAsync(
                        boundary.Owner!,
                        request.DefaultChatModelProfileId,
                        request.DefaultLightweightModelProfileId,
                        request.Timezone,
                        request.ApprovalDefaults?.GetRawText(),
                        request.MemoryControls?.GetRawText(),
                        request.ExpectedVersion,
                        token
                    );
                    return item is null
                        ? Problem(409, "version_conflict")
                        : Results.Json(SettingsDto(item));
                }
                catch (Exception exception)
                    when (exception
                            is TimeZoneNotFoundException
                                or ArgumentException
                                or JsonException
                    )
                {
                    return Problem(400, "invalid_request");
                }
            }
        );
        app.MapGet(
            "/api/v1/activity",
            async (
                HttpContext context,
                ITokenValidator validator,
                TesseraConfig config,
                IServiceProvider services,
                CancellationToken token
            ) =>
            {
                var boundary = await Boundary(context, validator, config, services, token);
                if (boundary.Error is not null)
                    return boundary.Error;
                return await Activity(context, boundary.Store!, boundary.Owner!, token);
            }
        );
    }

    private static async Task<IResult> RetryAsync(
        HttpContext context,
        string conversationId,
        RetryMessageRequest request,
        ProductBoundary boundary,
        ICredentialStore custody,
        IHttpTransport transport,
        CancellationToken token
    )
    {
        var conversation = await boundary.Store!.GetConversationAsync(
            boundary.Owner!,
            conversationId,
            token
        );
        if (conversation is null)
            return Problem(404, "not_found");
        var messages = await boundary.Store.ListMessagesAsync(
            boundary.Owner!,
            conversationId,
            token
        );
        var failedIndex = messages
            .ToList()
            .FindIndex(item =>
                item.MessageId == request.MessageId
                && item.Role == "ASSISTANT"
                && item.Status is "FAILED" or "STOPPED"
            );
        if (failedIndex < 0)
            return Problem(409, "invalid_state");
        var user = messages.Take(failedIndex).LastOrDefault(item => item.Role == "USER");
        if (user?.Parts.FirstOrDefault(item => item.Kind == "TEXT")?.Text is not string text)
            return Problem(409, "invalid_state");
        if (conversation.ModelProfileId is null)
            return Problem(422, "configuration_required");
        var profile = await boundary.Store.GetModelProfileAsync(
            boundary.Owner!,
            conversation.ModelProfileId,
            token
        );
        if (profile is null || !profile.Enabled)
            return Problem(422, "configuration_required");
        var account = await boundary.Store.GetConnectedAccountAsync(
            boundary.Owner!,
            profile.AccountId,
            token
        );
        if (account?.Lifecycle != AccountLifecycle.Connected)
            return Problem(422, "configuration_required");
        var bundle = await custody.GetBundleAsync(account.CredentialRef, token);
        if (!bundle.HasAccessToken)
            return Problem(422, "configuration_required");
        var now = DateTimeOffset.UtcNow;
        var execution = Guid.NewGuid().ToString("N");
        await boundary.Store.StartExecutionAsync(
            boundary.Owner!,
            conversationId,
            execution,
            user.MessageId,
            now,
            token
        );
        using var input = JsonDocument.Parse(JsonSerializer.Serialize(new { prompt = text }));
        var registry = new CapabilityRegistry();
        registry.Register(new ModelCapability(transport, profile, bundle.AccessToken!));
        var coordinator = new ExecutionCoordinator(
            registry,
            boundary.Store,
            boundary.Store,
            boundary.Store,
            boundary.Store,
            boundary.Store
        );
        var result = (
            await coordinator.ExecuteOrProposeAsync(
                new(
                    boundary.Owner!,
                    execution,
                    "model.chat.complete",
                    "1",
                    "model-provider",
                    "1",
                    profile.AccountId,
                    profile.Model,
                    ActionPayloadHash.Compute(System.Text.Encoding.UTF8.GetBytes(profile.Endpoint)),
                    input.RootElement.Clone(),
                    context.Request.Headers["Idempotency-Key"].FirstOrDefault()
                        ?? Guid.NewGuid().ToString("N"),
                    ConversationId: conversationId,
                    MessageId: user.MessageId
                ),
                now,
                token
            )
        ).Result!;
        var assistantId = Guid.NewGuid().ToString("N");
        var completed = DateTimeOffset.UtcNow;
        var succeeded = result.Outcome == CapabilityOutcome.Succeeded;
        await boundary.Store.AddMessageAsync(
            new(
                boundary.Owner!,
                assistantId,
                conversationId,
                "ASSISTANT",
                succeeded ? "COMPLETED" : "FAILED",
                request.MessageId,
                [
                    succeeded
                        ? new(
                            Guid.NewGuid().ToString("N"),
                            1,
                            "TEXT",
                            result.Output.GetProperty("text").GetString()
                        )
                        : new(
                            Guid.NewGuid().ToString("N"),
                            1,
                            "FAILURE",
                            null,
                            ErrorCode: result.FailureCode ?? "provider_unavailable"
                        ),
                ],
                now,
                completed,
                1
            ),
            token
        );
        await boundary.Store.CompleteExecutionAsync(
            boundary.Owner!,
            execution,
            succeeded ? "COMPLETED" : "FAILED",
            completed,
            token
        );
        return Results.Json(new { executionId = execution }, statusCode: 202);
    }

    private static async Task<CapabilityRegistry> ApprovalRegistry(
        SqliteKernelStore store,
        ICredentialStore custody,
        IHttpTransport transport,
        ExecutionRequest request,
        CancellationToken token,
        TesseraPluginRegistry? plugins = null,
        IMcpClientRuntime? mcpRuntime = null
    )
    {
        var registry = new CapabilityRegistry();
        if (
            request.PluginId == "local"
            && request.CapabilityVersion == "1"
            && request.AccountId is null
        )
        {
            if (request.CapabilityId == "local.memory.remember")
                registry.Register(new MemoryRememberCapability(store));
            else if (request.CapabilityId == "local.memory.correct")
                registry.Register(new MemoryCorrectCapability(store));
            else
                throw new InvalidOperationException("capability_unavailable");
            return registry;
        }
        if (plugins is null || !plugins.IsAuthoritative)
            throw new InvalidOperationException("plugin_runtime_unavailable");
        if (mcpRuntime is null)
            throw new InvalidOperationException("plugin_runtime_unavailable");
        if (!plugins.TryResolve(request.PluginId, request.PluginVersion, out _))
            throw new InvalidOperationException("plugin_module_unavailable");
        return await PluginRegistry(store, custody, transport, plugins, mcpRuntime, request, token);
    }

    private static async Task<CapabilityRegistry> ReadRegistry(
        SqliteKernelStore store,
        ICredentialStore custody,
        IHttpTransport transport,
        ExecutionRequest request,
        CancellationToken token,
        TesseraPluginRegistry? plugins = null,
        IMcpClientRuntime? mcpRuntime = null
    )
    {
        var registry = new CapabilityRegistry();
        if (
            request.CapabilityId == "local.time"
            && request.CapabilityVersion == "1"
            && request.PluginId == "local"
        )
        {
            registry.Register(new LocalTimeCapability());
            return registry;
        }
        if (
            request.CapabilityId == "local.memory.why"
            && request.CapabilityVersion == "1"
            && request.PluginId == "local"
            && request.AccountId is null
        )
        {
            registry.Register(new MemoryWhyCapability(store));
            return registry;
        }
        if (plugins is null || !plugins.IsAuthoritative)
            throw new InvalidOperationException("plugin_runtime_unavailable");
        if (mcpRuntime is null)
            throw new InvalidOperationException("plugin_runtime_unavailable");
        if (!plugins.TryResolve(request.PluginId, request.PluginVersion, out _))
            throw new InvalidOperationException("plugin_module_unavailable");
        return await PluginRegistry(store, custody, transport, plugins, mcpRuntime, request, token);
    }

    private static async Task<CapabilityRegistry> PluginRegistry(
        SqliteKernelStore store,
        ICredentialStore custody,
        IHttpTransport transport,
        TesseraPluginRegistry plugins,
        IMcpClientRuntime mcpRuntime,
        ExecutionRequest request,
        CancellationToken token
    )
    {
        ConnectedAccount? account = null;
        if (request.AccountId is not null)
        {
            account =
                await store.GetConnectedAccountAsync(
                    request.OwnerPrincipalId,
                    request.AccountId,
                    token
                ) ?? throw new InvalidOperationException("account_unavailable");
            ConnectedAccountCredentialRef.Validate(account, request.OwnerPrincipalId);
        }
        var context = new PluginCapabilityContext(
            account,
            CredentialBundle.Empty,
            transport,
            mcpRuntime,
            async (reference, cancellationToken) =>
                await custody.GetBundleAsync(reference, cancellationToken)
        );
        ICapability capability;
        try
        {
            capability = await plugins.CreateCapabilityAsync(
                request.PluginId,
                request.PluginVersion,
                request.CapabilityId,
                request.CapabilityVersion,
                context,
                token
            );
        }
        catch (PluginModuleException exception)
        {
            throw new InvalidOperationException(exception.ErrorCode, exception);
        }
        var registry = new CapabilityRegistry();
        registry.Register(capability);
        return registry;
    }

    private static async Task<IReadOnlyList<ActionRecord>> ActionsForRun(
        SqliteKernelStore store,
        string owner,
        string runId,
        CancellationToken token
    )
    {
        var values = new List<ActionRecord>();
        foreach (var state in Enum.GetValues<ActionState>())
            values.AddRange(
                (await store.ListByStateAsync(owner, state, token)).Where(item =>
                    item.R2Binding?.JobRunId == runId
                )
            );
        return values.OrderByDescending(item => item.CreatedAt).Take(100).ToArray();
    }

    private static async Task<IResult> ActionList(
        HttpContext context,
        SqliteKernelStore store,
        string owner,
        CancellationToken token
    )
    {
        var values = new List<ActionRecord>();
        foreach (var state in Enum.GetValues<ActionState>())
            values.AddRange(await store.ListByStateAsync(owner, state, token));
        var stateFilter = context.Request.Query["state"].ToString();
        if (stateFilter.Length > 0)
            values = values.Where(item => item.State.ToContractValue() == stateFilter).ToList();
        var conversation = context.Request.Query["conversationId"].ToString();
        var message = context.Request.Query["messageId"].ToString();
        var job = context.Request.Query["jobId"].ToString();
        var run = context.Request.Query["jobRunId"].ToString();
        var filtered = values.Where(item =>
            (conversation.Length == 0 || item.R2Binding?.ConversationId == conversation)
            && (message.Length == 0 || item.R2Binding?.MessageId == message)
            && (job.Length == 0 || item.R2Binding?.JobId == job)
            && (run.Length == 0 || item.R2Binding?.JobRunId == run)
        );
        if (bool.TryParse(context.Request.Query["approvalRequired"], out var approval))
            filtered = filtered.Where(item =>
                (
                    item.State == ActionState.Proposed
                    && item.R2Binding?.ExpiresAt > DateTimeOffset.UtcNow
                ) == approval
            );
        if (!TryBounds(context, out var from, out var to, out var error))
            return error!;
        var ordered = filtered
            .Where(item =>
                (from is null || item.CreatedAt >= from) && (to is null || item.CreatedAt <= to)
            )
            .OrderByDescending(item => item.CreatedAt)
            .ThenBy(item => item.ActionId)
            .Take(100)
            .ToArray();
        return Page(
            context,
            owner,
            await Task.WhenAll(ordered.Select(item => ActionDto(store, owner, item, token)))
        );
    }

    private static async Task<IResult> Activity(
        HttpContext context,
        SqliteKernelStore store,
        string owner,
        CancellationToken token
    )
    {
        var values = new List<ActivityItem>();
        foreach (var item in await ((IEventRepository)store).ListAsync(owner, token))
            values.Add(
                new(
                    item.EventId,
                    "event",
                    item.OccurredAt,
                    item.EventType,
                    null,
                    "event",
                    item.EventId,
                    item.EvidenceRefs
                )
            );
        foreach (var item in await store.ListMemoryAsync(owner, true, token))
            values.Add(
                new(
                    item.AssertionId,
                    "memory_change",
                    item.SupersededAt ?? item.CreatedAt,
                    $"{item.SubjectKey} {item.Predicate}",
                    item.EpistemicStatus.ToString(),
                    "memory",
                    item.AssertionId,
                    item.EvidenceRefs
                )
            );
        foreach (var state in Enum.GetValues<ActionState>())
        foreach (var item in await store.ListByStateAsync(owner, state, token))
            values.Add(
                new(
                    item.ActionId,
                    "action",
                    item.CompletedAt ?? item.StartedAt ?? item.CreatedAt,
                    item.Intent,
                    item.State.ToContractValue(),
                    "action",
                    item.ActionId,
                    []
                )
            );
        foreach (var item in await store.ListJobRunsAsync(owner, null, token))
            values.Add(
                new(
                    item.RunId,
                    "job_run",
                    item.EndedAt ?? item.StartedAt ?? item.ScheduledFor,
                    $"Job run {item.JobId}",
                    item.State,
                    "job_run",
                    item.RunId,
                    []
                )
            );
        var query = context.Request.Query["query"].ToString().Trim();
        if (query.Length > 200)
            return Problem(400, "invalid_request");
        var kind = context.Request.Query["kind"].ToString();
        var stateFilter = context.Request.Query["state"].ToString();
        if (!TryBounds(context, out var from, out var to, out var error))
            return error!;
        var filtered = values
            .Where(item =>
                (
                    query.Length == 0
                    || item.Summary.Contains(query, StringComparison.OrdinalIgnoreCase)
                )
                && (kind.Length == 0 || item.Kind == kind)
                && (
                    stateFilter.Length == 0
                    || string.Equals(item.State, stateFilter, StringComparison.OrdinalIgnoreCase)
                )
                && (from is null || item.OccurredAt >= from)
                && (to is null || item.OccurredAt <= to)
            )
            .OrderByDescending(item => item.OccurredAt)
            .ThenBy(item => item.Id);
        return Page(context, owner, filtered);
    }

    private static bool TryBounds(
        HttpContext context,
        out DateTimeOffset? from,
        out DateTimeOffset? to,
        out IResult? error
    )
    {
        from = null;
        to = null;
        error = null;
        var parsedFrom = default(DateTimeOffset);
        var parsedTo = default(DateTimeOffset);
        var fromText = context.Request.Query["from"].ToString();
        var toText = context.Request.Query["to"].ToString();
        if (
            (fromText.Length > 0 && !DateTimeOffset.TryParse(fromText, out parsedFrom))
            || (toText.Length > 0 && !DateTimeOffset.TryParse(toText, out parsedTo))
        )
        {
            error = Problem(400, "invalid_request");
            return false;
        }
        if (fromText.Length > 0)
            from = parsedFrom.ToUniversalTime();
        if (toText.Length > 0)
            to = parsedTo.ToUniversalTime();
        if (from > to)
        {
            error = Problem(400, "invalid_request");
            return false;
        }
        return true;
    }

    internal static async Task<ChatToolContext> ChatToolsAsync(
        SqliteKernelStore store,
        string owner,
        string conversationId,
        CancellationToken token,
        TesseraPluginRegistry? plugins = null
    )
    {
        var grants = await store.GetConversationGrantsAsync(owner, conversationId, token);
        var definitions = new List<object>();
        if (grants.Capabilities.Contains(("local.time", "1")))
            definitions.Add(
                new
                {
                    name = "current_time",
                    description = "Get the current date and time in an IANA time zone.",
                    parameters = new
                    {
                        type = "object",
                        properties = new { timeZone = new { type = "string" } },
                        required = TimeZoneRequired,
                        additionalProperties = false,
                    },
                }
            );
        if (grants.Capabilities.Contains(("local.memory.remember", "1")))
            definitions.Add(
                new
                {
                    name = "remember_memory",
                    description = "Propose durable user-authored memory only when the user explicitly asks Tessera to remember it. Human approval is required.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            subjectKey = new { type = "string" },
                            predicate = new { type = "string" },
                            value = new { type = "string" },
                        },
                        required = RememberRequired,
                        additionalProperties = false,
                    },
                }
            );
        if (grants.Capabilities.Contains(("local.memory.correct", "1")))
            definitions.Add(
                new
                {
                    name = "correct_memory",
                    description = "Propose a corrected value for an existing memory assertion when the user explicitly corrects it. Human approval is required.",
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            assertionId = new { type = "string" },
                            value = new { type = "string" },
                        },
                        required = CorrectRequired,
                        additionalProperties = false,
                    },
                }
            );
        if (grants.Capabilities.Contains(("local.memory.why", "1")))
            definitions.Add(
                new
                {
                    name = "why_memory",
                    description = "Explain a memory assertion using its durable evidence and history.",
                    parameters = new
                    {
                        type = "object",
                        properties = new { assertionId = new { type = "string" } },
                        required = AssertionRequired,
                        additionalProperties = false,
                    },
                }
            );
        var grantedAccounts = (await store.ListConnectedAccountsAsync(owner, token))
            .Where(account => grants.Accounts.Contains(account.AccountId, StringComparer.Ordinal))
            .ToArray();
        var enabledPlugins = (await store.ListPluginInstallationsAsync(owner, token))
            .Where(plugin => plugin.Enabled)
            .Select(plugin => (plugin.PluginId, plugin.PluginVersion))
            .ToHashSet();
        var pluginTools = plugins?.ProjectModelTools(
            grantedAccounts,
            grants.Capabilities.ToHashSet())
            .Where(tool => enabledPlugins.Contains((tool.PluginId, tool.PluginVersion)))
            .ToArray() ?? [];
        definitions.AddRange(pluginTools.Select(tool => (object)new
        {
            name = tool.Tool.Name,
            description = tool.Tool.Description,
            parameters = tool.Parameters,
        }));
        return new(definitions, pluginTools);
    }

    internal static async Task<ChatToolOutcome> ExecuteChatToolAsync(
        SqliteKernelStore store,
        ICredentialStore custody,
        IHttpTransport transport,
        string owner,
        string execution,
        string conversationId,
        string? messageId,
        ChatToolContext context,
        JsonElement call,
        int sequence,
        CancellationToken token,
        TesseraPluginRegistry? plugins = null,
        IMcpClientRuntime? mcpRuntime = null
    )
    {
        var callId =
            call.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("provider_malformed");
        var name =
            call.GetProperty("name").GetString()
            ?? throw new InvalidOperationException("provider_malformed");
        var arguments = call.GetProperty("arguments").Clone();
        var projected = context.PluginTools.SingleOrDefault(item => item.Tool.Name == name);
        if (projected is not null)
        {
            if (plugins is null || mcpRuntime is null)
                return ToolFailure(callId, sequence, "plugin_runtime_unavailable");
            return await ExecuteProjectedChatToolAsync(
                store,
                custody,
                transport,
                plugins,
                mcpRuntime,
                owner,
                execution,
                conversationId,
                messageId,
                callId,
                sequence,
                projected,
                arguments,
                token);
        }
        string capabilityId;
        string pluginId;
        string? accountId;
        string target;
        bool sideEffect;
        switch (name)
        {
            case "current_time":
                capabilityId = "local.time";
                pluginId = "local";
                accountId = null;
                target = arguments.TryGetProperty("timeZone", out var zone)
                    ? zone.GetString() ?? "UTC"
                    : "UTC";
                sideEffect = false;
                break;
            case "remember_memory":
                capabilityId = "local.memory.remember";
                pluginId = "local";
                accountId = null;
                target = "memory:user";
                _ = RequiredToolText(arguments, "subjectKey", 256);
                _ = RequiredToolText(arguments, "predicate", 256);
                _ = RequiredToolText(arguments, "value", 4096);
                sideEffect = true;
                break;
            case "correct_memory":
                capabilityId = "local.memory.correct";
                pluginId = "local";
                accountId = null;
                target = RequiredToolText(arguments, "assertionId", 256);
                _ = RequiredToolText(arguments, "value", 4096);
                sideEffect = true;
                break;
            case "why_memory":
                capabilityId = "local.memory.why";
                pluginId = "local";
                accountId = null;
                target = RequiredToolText(arguments, "assertionId", 256);
                sideEffect = false;
                break;
            default:
                return new(
                    new(callId, "{\"error\":\"tool_not_available\"}"),
                    new(
                        Guid.NewGuid().ToString("N"),
                        sequence,
                        "FAILURE",
                        null,
                        ErrorCode: "tool_not_available"
                    )
                );
        }
        var request = new ExecutionRequest(
            owner,
            $"{execution}:{callId}",
            capabilityId,
            "1",
            pluginId,
            "1.0.0",
            accountId,
            target,
            ActionPayloadHash.Compute(Encoding.UTF8.GetBytes(target)),
            arguments,
            $"{execution}:{callId}",
            conversationId,
            messageId
        );
        var registry = sideEffect
            ? await ApprovalRegistry(store, custody, transport, request, token)
            : await ReadRegistry(store, custody, transport, request, token);
        var coordinator = new ExecutionCoordinator(
            registry,
            store,
            store,
            store,
            store,
            store,
            store
        );
        var response = await coordinator.ExecuteOrProposeAsync(
            request,
            DateTimeOffset.UtcNow,
            token
        );
        if (response.ApprovalRequired && response.Action is not null)
        {
            var output = JsonSerializer.Serialize(
                new { status = "approval_required", actionId = response.Action.ActionId }
            );
            return new(
                new(callId, output),
                new(
                    Guid.NewGuid().ToString("N"),
                    sequence,
                    "ACTION",
                    null,
                    ActionId: response.Action.ActionId
                )
            );
        }
        if (response.Result is null || response.Result.Outcome != CapabilityOutcome.Succeeded)
        {
            var code = response.Result?.FailureCode ?? "capability_failed";
            if (code == "provider_auth_required" && accountId is not null)
                await MarkAccountAuthRequiredAsync(store, owner, accountId, token);
            return new(
                new(callId, JsonSerializer.Serialize(new { error = code })),
                new(
                    Guid.NewGuid().ToString("N"),
                    sequence,
                    "FAILURE",
                    null,
                    CapabilityCallId: callId,
                    ErrorCode: code
                )
            );
        }
        var outputJson = response.Result.Output.GetRawText();
        var now = DateTimeOffset.UtcNow;
        var evidenceId = $"evidence:capability:{execution}:{callId}";
        var excerpt = outputJson.Length <= 4096 ? outputJson : outputJson[..4096];
        var evidence = EvidenceRecord.Create(
            evidenceId,
            owner,
            "capability.result",
            callId,
            $"tessera://capability/{capabilityId}/{callId}",
            now,
            now,
            "sha256",
            1,
            ActionPayloadHash.Compute(Encoding.UTF8.GetBytes(outputJson)),
            RetentionState.Active,
            SensitivityClass.Internal,
            ProducerRef.Create($"plugin:{pluginId}", "1.0.0"),
            1,
            excerpt
        );
        await ((IEvidenceRepository)store).AddAsync(owner, evidence, token);
        await store.AttachCapabilityEvidenceAsync(
            owner,
            $"{execution}:{callId}",
            evidenceId,
            token
        );
        return new(
            new(callId, outputJson),
            new(
                Guid.NewGuid().ToString("N"),
                sequence,
                "CAPABILITY_RESULT",
                null,
                callId,
                evidenceId,
                null,
                [evidenceId]
            )
        );
    }

    private static async Task<ChatToolOutcome> ExecuteProjectedChatToolAsync(
        SqliteKernelStore store,
        ICredentialStore custody,
        IHttpTransport transport,
        TesseraPluginRegistry plugins,
        IMcpClientRuntime mcpRuntime,
        string owner,
        string execution,
        string conversationId,
        string? messageId,
        string callId,
        int sequence,
        ProjectedModelTool projected,
        JsonElement arguments,
        CancellationToken token)
    {
        PluginModelToolBinding binding;
        try { binding = plugins.BindModelTool(projected, arguments); }
        catch (PluginModuleException exception) { return ToolFailure(callId, sequence, exception.ErrorCode); }
        var input = binding.Input;
        if (projected.Tool.ProposalCapabilityId is not null)
        {
            var proposalRequest = new ExecutionRequest(
                owner,
                $"{execution}:{callId}:proposal",
                projected.Tool.ProposalCapabilityId,
                projected.Tool.ProposalCapabilityVersion!,
                projected.PluginId,
                projected.PluginVersion,
                binding.AccountId,
                binding.TargetScope,
                ActionPayloadHash.Compute(Encoding.UTF8.GetBytes(binding.TargetScope)),
                input,
                $"{execution}:{callId}:proposal",
                conversationId,
                messageId);
            var proposalRegistry = await ReadRegistry(store, custody, transport, proposalRequest, token, plugins, mcpRuntime);
            var proposalCoordinator = new ExecutionCoordinator(proposalRegistry, store, store, store, store, store, store);
            var proposal = await proposalCoordinator.ExecuteOrProposeAsync(proposalRequest, DateTimeOffset.UtcNow, token);
            if (proposal.Result is null || proposal.Result.Outcome != CapabilityOutcome.Succeeded)
                return ToolFailure(callId, sequence, proposal.Result?.FailureCode ?? "provider_preflight_failed");
            input = proposal.Result.Output;
        }
        var request = new ExecutionRequest(
            owner,
            $"{execution}:{callId}",
            projected.Capability.CapabilityId,
            projected.Capability.Version,
            projected.PluginId,
            projected.PluginVersion,
            binding.AccountId,
            binding.TargetScope,
            ActionPayloadHash.Compute(Encoding.UTF8.GetBytes(binding.TargetScope)),
            input,
            $"{execution}:{callId}",
            conversationId,
            messageId);
        var registry = projected.Capability.SideEffectClass == SideEffectClass.ReadOnly
            ? await ReadRegistry(store, custody, transport, request, token, plugins, mcpRuntime)
            : await ApprovalRegistry(store, custody, transport, request, token, plugins, mcpRuntime);
        var coordinator = new ExecutionCoordinator(registry, store, store, store, store, store, store);
        var response = await coordinator.ExecuteOrProposeAsync(request, DateTimeOffset.UtcNow, token);
        if (response.ApprovalRequired && response.Action is not null)
            return new(
                new(callId, JsonSerializer.Serialize(new { status = "approval_required", actionId = response.Action.ActionId })),
                new(Guid.NewGuid().ToString("N"), sequence, "ACTION", null, ActionId: response.Action.ActionId));
        if (response.Result is null || response.Result.Outcome != CapabilityOutcome.Succeeded)
        {
            var code = response.Result?.FailureCode ?? "capability_failed";
            if (code == "provider_auth_required" && binding.AccountId is not null)
                await MarkAccountAuthRequiredAsync(store, owner, binding.AccountId, token);
            return ToolFailure(callId, sequence, code);
        }
        var output = response.Result.Output.GetRawText();
        var now = DateTimeOffset.UtcNow;
        var evidenceId = $"evidence:capability:{execution}:{callId}";
        var sensitivity = projected.Capability.AllowedDataClasses.DefaultIfEmpty(SensitivityClass.Internal).Max();
        var excerpt = sensitivity >= SensitivityClass.Confidential ? null : output.Length <= 4096 ? output : output[..4096];
        await ((IEvidenceRepository)store).AddAsync(owner, EvidenceRecord.Create(
            evidenceId,
            owner,
            "capability.result",
            callId,
            $"tessera://capability/{projected.Capability.CapabilityId}/{callId}",
            now,
            now,
            "sha256",
            1,
            ActionPayloadHash.Compute(Encoding.UTF8.GetBytes(output)),
            RetentionState.Active,
            sensitivity,
            ProducerRef.Create($"plugin:{projected.PluginId}", projected.PluginVersion),
            1,
            excerpt), token);
        await store.AttachCapabilityEvidenceAsync(owner, $"{execution}:{callId}", evidenceId, token);
        return new(
            new(callId, output),
            new(Guid.NewGuid().ToString("N"), sequence, "CAPABILITY_RESULT", null, callId, evidenceId, null, [evidenceId]));
    }

    private static ChatToolOutcome ToolFailure(string callId, int sequence, string code)
        => new(
            new(callId, JsonSerializer.Serialize(new { error = code })),
            new(Guid.NewGuid().ToString("N"), sequence, "FAILURE", null, CapabilityCallId: callId, ErrorCode: code));

    internal static async Task<JobToolContext> JobToolsAsync(
        SqliteKernelStore store,
        ProductJob job,
        CancellationToken token,
        TesseraPluginRegistry? plugins = null
    )
    {
        var definitions = new List<object>();
        if (job.CapabilityGrants.Contains(("local.time", "1")))
            definitions.Add(
                new
                {
                    name = "current_time",
                    description = "Get the current date and time in an IANA time zone.",
                    parameters = new
                    {
                        type = "object",
                        properties = new { timeZone = new { type = "string" } },
                        required = TimeZoneRequired,
                        additionalProperties = false,
                    },
                }
            );
        var grantedAccounts = new List<ConnectedAccount>();
        foreach (var accountId in job.AccountGrants)
            if (await store.GetConnectedAccountAsync(job.OwnerPrincipalId, accountId, token) is { } account)
                grantedAccounts.Add(account);
        var enabledPlugins = (await store.ListPluginInstallationsAsync(job.OwnerPrincipalId, token))
            .Where(plugin => plugin.Enabled)
            .Select(plugin => (plugin.PluginId, plugin.PluginVersion))
            .ToHashSet();
        var pluginTools = plugins?.ProjectModelTools(
            grantedAccounts,
            job.CapabilityGrants.ToHashSet(),
            job.SideEffectGrants.ToHashSet(StringComparer.Ordinal),
            forJob: true)
            .Where(tool => enabledPlugins.Contains((tool.PluginId, tool.PluginVersion)))
            .ToArray() ?? [];
        definitions.AddRange(pluginTools.Select(tool => (object)new
        {
            name = tool.Tool.Name,
            description = tool.Tool.Description,
            parameters = tool.Parameters,
        }));
        return new(definitions, pluginTools);
    }

    internal static async Task<JobToolOutcome> ExecuteJobToolAsync(
        SqliteKernelStore store,
        ICredentialStore custody,
        IHttpTransport transport,
        ProductJob job,
        ProductJobRun run,
        long fence,
        JobToolContext context,
        JsonElement call,
        CancellationToken token,
        TesseraPluginRegistry? plugins = null,
        IMcpClientRuntime? mcpRuntime = null
    )
    {
        var callId =
            call.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("provider_malformed");
        var name =
            call.GetProperty("name").GetString()
            ?? throw new InvalidOperationException("provider_malformed");
        var arguments = call.GetProperty("arguments").Clone();
        var projected = context.PluginTools.SingleOrDefault(item => item.Tool.Name == name);
        if (projected is not null)
        {
            if (plugins is null || mcpRuntime is null)
                return new(new(callId, "{\"error\":\"plugin_runtime_unavailable\"}"), false, "plugin_runtime_unavailable");
            return await ExecuteProjectedJobToolAsync(
                store,
                custody,
                transport,
                plugins,
                mcpRuntime,
                job,
                run,
                fence,
                callId,
                projected,
                arguments,
                token);
        }
        string capabilityId;
        string pluginId;
        string? accountId;
        string target;
        bool sideEffect;
        switch (name)
        {
            case "current_time" when job.CapabilityGrants.Contains(("local.time", "1")):
                capabilityId = "local.time";
                pluginId = "local";
                accountId = null;
                target = arguments.TryGetProperty("timeZone", out var zone)
                    ? zone.GetString() ?? "UTC"
                    : "UTC";
                sideEffect = false;
                break;
            default:
                return new(
                    new(callId, "{\"error\":\"tool_not_granted\"}"),
                    false,
                    "tool_not_granted"
                );
        }
        var request = new ExecutionRequest(
            job.OwnerPrincipalId,
            $"{run.RunId}:{callId}",
            capabilityId,
            "1",
            pluginId,
            "1.0.0",
            accountId,
            target,
            ActionPayloadHash.Compute(Encoding.UTF8.GetBytes(target)),
            arguments,
            $"{run.RunId}:{callId}",
            JobId: job.JobId,
            JobRunId: run.RunId
        );
        var registry = sideEffect
            ? await ApprovalRegistry(store, custody, transport, request, token)
            : await ReadRegistry(store, custody, transport, request, token);
        var coordinator = new ExecutionCoordinator(
            registry,
            store,
            store,
            store,
            store,
            store,
            store
        );
        var response = await coordinator.ExecuteOrProposeAsync(
            request,
            DateTimeOffset.UtcNow,
            token
        );
        if (response.ApprovalRequired && response.Action is not null)
        {
            if (
                !await store.WaitForRunApprovalAsync(
                    job.OwnerPrincipalId,
                    run.RunId,
                    fence,
                    response.Action.ActionId,
                    request,
                    DateTimeOffset.UtcNow,
                    token
                )
            )
                throw new ProductConcurrencyException(
                    "Job run changed before approval wait was persisted."
                );
            return new(
                new(
                    callId,
                    JsonSerializer.Serialize(
                        new { status = "approval_required", actionId = response.Action.ActionId }
                    )
                ),
                true,
                null
            );
        }
        if (response.Result is null || response.Result.Outcome != CapabilityOutcome.Succeeded)
        {
            var code = response.Result?.FailureCode ?? "capability_failed";
            if (code == "provider_auth_required" && accountId is not null)
                await MarkAccountAuthRequiredAsync(store, job.OwnerPrincipalId, accountId, token);
            return new(new(callId, JsonSerializer.Serialize(new { error = code })), false, code);
        }
        var output = response.Result.Output.GetRawText();
        var now = DateTimeOffset.UtcNow;
        var evidenceId = $"evidence:capability:{run.RunId}:{callId}";
        var excerpt = output.Length <= 4096 ? output : output[..4096];
        await ((IEvidenceRepository)store).AddAsync(
            job.OwnerPrincipalId,
            EvidenceRecord.Create(
                evidenceId,
                job.OwnerPrincipalId,
                "capability.result",
                callId,
                $"tessera://capability/{capabilityId}/{callId}",
                now,
                now,
                "sha256",
                1,
                ActionPayloadHash.Compute(Encoding.UTF8.GetBytes(output)),
                RetentionState.Active,
                SensitivityClass.Internal,
                ProducerRef.Create($"plugin:{pluginId}", "1.0.0"),
                1,
                excerpt
            ),
            token
        );
        await store.AttachCapabilityEvidenceAsync(
            job.OwnerPrincipalId,
            $"{run.RunId}:{callId}",
            evidenceId,
            token
        );
        var checkpoints = await store.ListJobRunCheckpointsAsync(
            job.OwnerPrincipalId,
            run.RunId,
            token
        );
        await store.AddRunCheckpointAsync(
            job.OwnerPrincipalId,
            run.RunId,
            checkpoints.Count + 1,
            "capability_result",
            JsonSerializer.Serialize(
                new
                {
                    callId,
                    capabilityId,
                    evidenceId,
                }
            ),
            fence,
            now,
            token
        );
        return new(new(callId, output), false, null);
    }

    private static async Task<JobToolOutcome> ExecuteProjectedJobToolAsync(
        SqliteKernelStore store,
        ICredentialStore custody,
        IHttpTransport transport,
        TesseraPluginRegistry plugins,
        IMcpClientRuntime mcpRuntime,
        ProductJob job,
        ProductJobRun run,
        long fence,
        string callId,
        ProjectedModelTool projected,
        JsonElement arguments,
        CancellationToken token)
    {
        PluginModelToolBinding binding;
        try { binding = plugins.BindModelTool(projected, arguments); }
        catch (PluginModuleException exception)
        { return new(new(callId, JsonSerializer.Serialize(new { error = exception.ErrorCode })), false, exception.ErrorCode); }
        var request = new ExecutionRequest(
            job.OwnerPrincipalId,
            $"{run.RunId}:{callId}",
            projected.Capability.CapabilityId,
            projected.Capability.Version,
            projected.PluginId,
            projected.PluginVersion,
            binding.AccountId,
            binding.TargetScope,
            ActionPayloadHash.Compute(Encoding.UTF8.GetBytes(binding.TargetScope)),
            binding.Input,
            $"{run.RunId}:{callId}",
            JobId: job.JobId,
            JobRunId: run.RunId);
        var registry = projected.Capability.SideEffectClass == SideEffectClass.ReadOnly
            ? await ReadRegistry(store, custody, transport, request, token, plugins, mcpRuntime)
            : await ApprovalRegistry(store, custody, transport, request, token, plugins, mcpRuntime);
        var coordinator = new ExecutionCoordinator(registry, store, store, store, store, store, store);
        var response = await coordinator.ExecuteOrProposeAsync(request, DateTimeOffset.UtcNow, token);
        if (response.ApprovalRequired && response.Action is not null)
        {
            if (!await store.WaitForRunApprovalAsync(job.OwnerPrincipalId, run.RunId, fence, response.Action.ActionId, request, DateTimeOffset.UtcNow, token))
                throw new ProductConcurrencyException("Job run changed before approval wait was persisted.");
            return new(new(callId, JsonSerializer.Serialize(new { status = "approval_required", actionId = response.Action.ActionId })), true, null);
        }
        if (response.Result is null || response.Result.Outcome != CapabilityOutcome.Succeeded)
        {
            var code = response.Result?.FailureCode ?? "capability_failed";
            if (code == "provider_auth_required" && binding.AccountId is not null)
                await MarkAccountAuthRequiredAsync(store, job.OwnerPrincipalId, binding.AccountId, token);
            return new(new(callId, JsonSerializer.Serialize(new { error = code })), false, code);
        }
        var output = response.Result.Output.GetRawText();
        var now = DateTimeOffset.UtcNow;
        var evidenceId = $"evidence:capability:{run.RunId}:{callId}";
        var sensitivity = projected.Capability.AllowedDataClasses.DefaultIfEmpty(SensitivityClass.Internal).Max();
        var excerpt = sensitivity >= SensitivityClass.Confidential ? null : output.Length <= 4096 ? output : output[..4096];
        await ((IEvidenceRepository)store).AddAsync(job.OwnerPrincipalId, EvidenceRecord.Create(
            evidenceId,
            job.OwnerPrincipalId,
            "capability.result",
            callId,
            $"tessera://capability/{projected.Capability.CapabilityId}/{callId}",
            now,
            now,
            "sha256",
            1,
            ActionPayloadHash.Compute(Encoding.UTF8.GetBytes(output)),
            RetentionState.Active,
            sensitivity,
            ProducerRef.Create($"plugin:{projected.PluginId}", projected.PluginVersion),
            1,
            excerpt), token);
        await store.AttachCapabilityEvidenceAsync(job.OwnerPrincipalId, $"{run.RunId}:{callId}", evidenceId, token);
        var checkpoints = await store.ListJobRunCheckpointsAsync(job.OwnerPrincipalId, run.RunId, token);
        await store.AddRunCheckpointAsync(
            job.OwnerPrincipalId,
            run.RunId,
            checkpoints.Count + 1,
            "capability_result",
            JsonSerializer.Serialize(new { callId, capabilityId = projected.Capability.CapabilityId, evidenceId }),
            fence,
            now,
            token);
        return new(new(callId, output), false, null);
    }

    private static string RequiredToolText(JsonElement arguments, string name, int maximum)
    {
        if (
            !arguments.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
        )
            throw new InvalidOperationException("provider_malformed");
        return ProductContentValidation.Text(value.GetString() ?? string.Empty, name, maximum);
    }

    internal static async Task MarkAccountAuthRequiredAsync(
        SqliteKernelStore store,
        string owner,
        string accountId,
        CancellationToken token
    )
    {
        var account = await store.GetConnectedAccountAsync(owner, accountId, token);
        if (account is null || account.Lifecycle == AccountLifecycle.Revoked)
            return;
        try
        {
            await store.SetConnectedAccountStateAsync(
                owner,
                accountId,
                account.Version,
                AccountLifecycle.AuthRequired,
                AccountHealth.AuthRequired,
                token
            );
            await store.SetJobsHealthForAccountAsync(owner, accountId, "BLOCKED", token);
        }
        catch (ProductConcurrencyException) { }
    }

    private static bool JobRequestMatches(
        ProductJob job,
        string name,
        string instruction,
        string desiredState,
        string profileId,
        JobSchedule schedule,
        string contextJson,
        string[] accounts,
        (string Id, string Version)[] capabilities,
        string[] effects
    ) =>
        job.Name == name
        && job.Instruction == instruction
        && job.DesiredState == desiredState
        && job.ModelProfileId == profileId
        && JsonSerializer.Serialize(job.Schedule) == JsonSerializer.Serialize(schedule)
        && job.ContextPolicyJson == contextJson
        && job.AccountGrants.Order().SequenceEqual(accounts)
        && job.CapabilityGrants.OrderBy(item => item.Id)
            .ThenBy(item => item.Version)
            .SequenceEqual(capabilities)
        && job.SideEffectGrants.Order().SequenceEqual(effects);

    private static bool TryIdempotencyKey(HttpContext context, out string? key)
    {
        key = context.Request.Headers["Idempotency-Key"].FirstOrDefault();
        return key is { Length: > 0 and <= 128 }
            && key.All(character => character is >= '!' and <= '~');
    }

    private static string RouteId(string owner, string scope, string kind, string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{owner}\n{scope}\n{kind}\n{key}"));
        return Convert.ToHexStringLower(hash);
    }

    private static async Task<object> JobRunDetail(
        SqliteKernelStore store,
        string owner,
        ProductJobRun run,
        CancellationToken token
    )
    {
        var actionRecords = await ActionsForRun(store, owner, run.RunId, token);
        var actions = await Task.WhenAll(
            actionRecords.Select(item => ActionDto(store, owner, item, token))
        );
        var outputs = await store.ListJobRunOutputsAsync(owner, run.RunId, token);
        var calls = await store.ListCapabilityCallsAsync(owner, run.RunId, token);
        var results = await store.ListCapabilityResultsAsync(owner, run.RunId, token);
        var evidenceRefs = results.SelectMany(result => result.EvidenceRefs).Distinct().ToArray();
        var evidence = new List<object>();
        foreach (var reference in evidenceRefs)
            if (await ((IEvidenceRepository)store).GetAsync(owner, reference, token) is { } item)
                evidence.Add(EvidenceDto(item));
        var trace = (await store.ListJobRunCheckpointsAsync(owner, run.RunId, token))
            .Select(item => new
            {
                sequence = item.Sequence,
                occurredAt = item.CreatedAt,
                type = item.Step,
                summary = item.Step,
                actionId = CheckpointActionId(item.StateJson),
            })
            .ToArray();
        return new
        {
            run = JobRunDto(run, calls, actionRecords, outputs, evidenceRefs),
            contextSnapshot = run.ContextSnapshotRef is null
                ? null
                : (object)new { snapshotRef = run.ContextSnapshotRef },
            capabilityUses = new
            {
                items = calls.Select(CapabilityCallDto).ToArray(),
                nextCursor = (string?)null,
            },
            accountUses = new
            {
                items = calls
                    .Where(call => call.AccountId is not null)
                    .Select(call => new
                    {
                        callId = call.CallId,
                        accountId = call.AccountId,
                        call.CapabilityId,
                        call.State,
                        call.CreatedAt,
                    })
                    .ToArray(),
                nextCursor = (string?)null,
            },
            actions = new { items = actions, nextCursor = (string?)null },
            outputs = new { items = outputs, nextCursor = (string?)null },
            evidence = new { items = evidence.ToArray(), nextCursor = (string?)null },
            trace = new { items = trace, nextCursor = (string?)null },
        };
    }

    private static string? CheckpointActionId(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("actionId", out var value)
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IResult Page<T>(HttpContext context, string owner, IEnumerable<T> items)
    {
        var offset = context.Items["r2.cursor.offset"] is int value ? value : 0;
        var limit = context.Items["r2.cursor.limit"] is int size ? size : 25;
        var page = items.Skip(offset).Take(limit + 1).ToArray();
        return Results.Json(
            new
            {
                items = page.Take(limit).ToArray(),
                nextCursor = page.Length > limit
                    ? Cursor(context, owner, offset + limit, limit)
                    : null,
            }
        );
    }

    private static bool TryCursor(HttpContext context, string owner, out int offset)
    {
        offset = 0;
        var limitText = context.Request.Query["limit"].ToString();
        var limit =
            limitText.Length == 0 ? 25
            : int.TryParse(limitText, out var parsed) ? parsed
            : 0;
        if (limit is < 1 or > 100)
            return false;
        context.Items["r2.cursor.limit"] = limit;
        var value = context.Request.Query["cursor"].ToString();
        if (value.Length == 0)
            return true;
        try
        {
            var padded = value
                .Replace('-', '+')
                .Replace('_', '/')
                .PadRight((value.Length + 3) / 4 * 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(padded)).Split(':');
            if (
                parts.Length != 3
                || !int.TryParse(parts[0], out offset)
                || offset < 0
                || !int.TryParse(parts[1], out var cursorLimit)
                || cursorLimit != limit
            )
                return false;
            var expected = CursorSignature(context, owner, offset, limit);
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected),
                Encoding.UTF8.GetBytes(parts[2])
            );
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Cursor(HttpContext context, string owner, int offset, int limit) =>
        Convert
            .ToBase64String(
                Encoding.UTF8.GetBytes(
                    $"{offset}:{limit}:{CursorSignature(context, owner, offset, limit)}"
                )
            )
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string CursorSignature(HttpContext context, string owner, int offset, int limit)
    {
        var signer = context.RequestServices.GetRequiredService<R2CursorSigner>();
        return Convert.ToHexStringLower(
            HMACSHA256.HashData(signer.Key, Encoding.UTF8.GetBytes($"{owner}\n{offset}\n{limit}"))
        )[..32];
    }

    private static object ConversationDto(Conversation item) =>
        new
        {
            id = item.ConversationId,
            conversationId = item.ConversationId,
            item.Title,
            item.State,
            item.ModelProfileId,
            item.CreatedAt,
            item.UpdatedAt,
            item.Version,
        };

    private static object MessageDto(ChatMessage item) =>
        new
        {
            id = item.MessageId,
            messageId = item.MessageId,
            item.ConversationId,
            item.Role,
            item.Status,
            parts = item
                .Parts.Select(part => new
                {
                    id = part.PartId,
                    part.Kind,
                    part.Text,
                    part.CapabilityCallId,
                    part.CapabilityResultId,
                    part.ActionId,
                    evidenceRefs = part.EvidenceRefs ?? [],
                    part.ErrorCode,
                })
                .ToArray(),
            item.CreatedAt,
            item.CompletedAt,
            item.RetryOf,
            item.Version,
        };

    private static object MemoryDto(AssertionRecord item) =>
        new
        {
            item.AssertionId,
            item.SubjectKey,
            item.Predicate,
            item.Value,
            status = item.EpistemicStatus.ToString(),
            item.ValidFrom,
            item.ValidTo,
            evidenceRefs = item.EvidenceRefs,
            version = 1,
        };

    private static object AccountDto(ConnectedAccount item) =>
        new
        {
            id = item.AccountId,
            accountId = item.AccountId,
            item.ProviderId,
            item.PluginId,
            item.DisplayName,
            item.ProviderAccountId,
            item.IdentityHint,
            lifecycle = item.Lifecycle.ToContractValue(),
            permissions = item.Permissions,
            providerScopes = item.ProviderScopes,
            capabilityIds = item
                .CapabilityBindings.Select(binding => binding.CapabilityId)
                .Distinct()
                .ToArray(),
            health = item.Health.ToContractValue(),
            item.LastSuccessfulUse,
            item.Version,
        };

    private static object PluginDto(PluginInstallation item) =>
        new
        {
            id = item.PluginId,
            pluginId = item.PluginId,
            name = item.Name,
            version = item.PluginVersion,
            pluginVersion = item.PluginVersion,
            item.Publisher,
            item.Enabled,
            item.PackageHash,
            configurationState = PluginRequiresAccount(item.ManifestJson)
                ? "ACCOUNT_SCOPED"
                : "NOT_REQUIRED",
            accountProviderIds = Array.Empty<string>(),
            capabilities = PluginCapabilities(item.ManifestJson),
            versionStamp = item.Version,
        };

    private static object SettingsDto(ProductSettings item) =>
        new
        {
            item.DefaultChatModelProfileId,
            item.DefaultLightweightModelProfileId,
            timezone = item.Timezone,
            approvalDefaults = JsonDocument.Parse(item.ApprovalDefaultsJson).RootElement.Clone(),
            memoryControls = JsonDocument.Parse(item.MemoryControlsJson).RootElement.Clone(),
            item.Version,
        };

    private static object JobDto(ProductJob item, ProductJobRun? lastRun = null) =>
        new
        {
            id = item.JobId,
            jobId = item.JobId,
            item.Name,
            item.Instruction,
            item.DesiredState,
            item.Health,
            item.ModelProfileId,
            item.Schedule,
            item.NextOccurrence,
            accountGrants = item.AccountGrants,
            capabilityGrants = item
                .CapabilityGrants.Select(value => $"{value.Id}@{value.Version}")
                .ToArray(),
            sideEffectGrants = item.SideEffectGrants,
            contextPolicy = JsonDocument.Parse(item.ContextPolicyJson).RootElement.Clone(),
            lastRun = lastRun is null ? null : JobRunDto(lastRun),
            item.Version,
        };

    private static object JobRunDto(
        ProductJobRun item,
        IReadOnlyList<ProductCapabilityCall>? calls = null,
        IReadOnlyList<ActionRecord>? actions = null,
        IReadOnlyList<JobRunOutput>? outputs = null,
        IReadOnlyList<string>? evidenceRefs = null
    ) =>
        new
        {
            id = item.RunId,
            runId = item.RunId,
            item.JobId,
            item.ScheduledFor,
            item.State,
            item.StartedAt,
            item.EndedAt,
            item.ModelProfileId,
            item.ContextSnapshotRef,
            capabilityCallIds = calls?.Select(call => call.CallId).ToArray() ?? [],
            accountIds = calls
                ?.Where(call => call.AccountId is not null)
                .Select(call => call.AccountId!)
                .Distinct()
                .ToArray()
                ?? [],
            actionIds = actions?.Select(action => action.ActionId).ToArray() ?? [],
            outputRefs = outputs?.Select(output => output.OutputRef).ToArray() ?? [],
            evidenceRefs = evidenceRefs ?? [],
            item.ErrorCode,
            item.Version,
        };

    private static object EvidenceDto(EvidenceRecord item) =>
        new
        {
            item.EvidenceId,
            item.SourceType,
            item.SourceLocator,
            item.ObservedAt,
            item.SourceTimestamp,
            item.BoundedExcerpt,
            item.ContentReference,
        };

    private static object CapabilityCallDto(ProductCapabilityCall item) =>
        new
        {
            id = item.CallId,
            callId = item.CallId,
            item.ExecutionId,
            item.ConversationId,
            item.MessageId,
            item.JobId,
            item.JobRunId,
            item.PluginId,
            item.PluginVersion,
            item.CapabilityId,
            item.CapabilityVersion,
            item.AccountId,
            item.State,
            item.CreatedAt,
            item.CompletedAt,
            item.ErrorCode,
            item.Version,
        };

    private static async Task<object> ActionDto(
        SqliteKernelStore store,
        string owner,
        ActionRecord item,
        CancellationToken token
    )
    {
        var request = await ((IDurableExecutionRequestRepository)store).GetAsync(
            owner,
            item.ActionId,
            token
        );
        return new
        {
            id = item.ActionId,
            item.R2Binding?.ConversationId,
            item.R2Binding?.MessageId,
            item.R2Binding?.JobId,
            item.R2Binding?.JobRunId,
            item.R2Binding?.PluginId,
            item.R2Binding?.PluginVersion,
            item.CapabilityId,
            item.CapabilityVersion,
            item.R2Binding?.AccountId,
            target = item.TargetScope,
            payloadPreview = request?.Input.Clone(),
            state = item.State.ToContractValue(),
            item.R2Binding?.ExpiresAt,
            item.ProviderReceipt,
            item.VerificationState,
            failureCode = item.Failure,
            item.Version,
        };
    }

    private static object[] PluginCapabilities(string manifestJson)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<PluginManifest>(manifestJson);
            return manifest
                    ?.Capabilities.Select(capability =>
                        (object)
                            new
                            {
                                id = capability.Id,
                                version = capability.Version,
                                description = capability.Description,
                                executorKind = capability.ExecutorKind,
                                accountRequired = capability.AccountRequired,
                                requiredPermissions = capability.RequiredPermissions,
                                sideEffectClass = capability.SideEffectClass,
                                timeoutMilliseconds = capability.TimeoutMilliseconds,
                                maxResultBytes = capability.MaxResultBytes,
                            }
                    )
                    .ToArray()
                ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool PluginRequiresAccount(string manifestJson)
    {
        try
        {
            using var document = JsonDocument.Parse(manifestJson);
            if (
                !document.RootElement.TryGetProperty("capabilities", out var values)
                && !document.RootElement.TryGetProperty("Capabilities", out values)
            )
                return false;
            return values
                .EnumerateArray()
                .Any(item =>
                    (
                        item.TryGetProperty("accountRequired", out var required)
                        || item.TryGetProperty("AccountRequired", out required)
                    )
                    && required.ValueKind == JsonValueKind.True
                );
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static (
        string ProviderId,
        string PluginVersion,
        string[] Permissions,
        AccountCapabilityBinding[] Capabilities
    ) AccountDefinition(CreateAccountRequest request, TesseraPluginRegistry plugins)
    {
        var pluginVersion = request.NonSecretConfig.TryGetProperty(
            "pluginVersion",
            out var versionValue
        )
            ? versionValue.GetString() ?? "1.0.0"
            : "1.0.0";
        if (request.PluginId == "model-provider")
            return (
                "openai-compatible",
                pluginVersion,
                [],
                [new("model-provider", pluginVersion, "model.chat.complete", "1")]
            );
        try
        {
            var definition = plugins.DefineAccount(request.PluginId, pluginVersion, request.NonSecretConfig);
            return (
                definition.ProviderId,
                pluginVersion,
                definition.InitialPermissions.ToArray(),
                definition.CapabilityBindings.ToArray());
        }
        catch (PluginModuleException exception)
        {
            throw new ArgumentException(exception.ErrorCode, nameof(request), exception);
        }
    }

    private static async Task<ConnectedAccount> ValidateAccountAsync(
        SqliteKernelStore store,
        string owner,
        ConnectedAccount account,
        CredentialBundle credential,
        IHttpTransport transport,
        TesseraPluginRegistry plugins,
        IMcpClientRuntime mcpRuntime,
        ICredentialStore custody,
        CancellationToken token
    )
    {
        if (account.ProviderId == "openai-compatible")
        {
            using var configuration = JsonDocument.Parse(account.NonSecretConfigJson);
            var endpoint =
                configuration.RootElement.GetProperty("endpoint").GetString() ?? string.Empty;
            var trustedInternal =
                configuration.RootElement.TryGetProperty("gatewayId", out var gatewayId)
                && gatewayId.ValueKind == JsonValueKind.String;
            var adapter = new OpenAiCompatibleAdapter(transport);
            var result = trustedInternal
                ? await adapter.ProbeTrustedInternalAsync(endpoint, credential.AccessToken!, token)
                : await adapter.ProbeAsync(
                    endpoint,
                    credential.AccessToken!,
                    endpoint.StartsWith("http://127.0.0.1", StringComparison.Ordinal)
                        || endpoint.StartsWith("http://localhost", StringComparison.Ordinal),
                    token
                );
            var auth = result.ErrorCode == "provider_auth_required";
            return await store.SetConnectedAccountStateAsync(
                owner,
                account.AccountId,
                account.Version,
                result.Available ? AccountLifecycle.Connected
                    : auth ? AccountLifecycle.AuthRequired
                    : AccountLifecycle.Degraded,
                result.Available ? AccountHealth.Healthy
                    : auth ? AccountHealth.AuthRequired
                    : AccountHealth.Degraded,
                token
            );
        }
        var context = new PluginCapabilityContext(
            account,
            credential,
            transport,
            mcpRuntime,
            async (reference, cancellationToken) =>
                await custody.GetBundleAsync(reference, cancellationToken));
        try
        {
            var validation = await plugins.ValidateAccountAsync(account, context, token);
            if (validation.ProviderAccountId is null || validation.IdentityHint is null)
                return await store.SetConnectedAccountStateAsync(
                    owner,
                    account.AccountId,
                    account.Version,
                    validation.Lifecycle,
                    validation.Health,
                    token);
            return await store.SetConnectedAccountValidationAsync(
                owner,
                account.AccountId,
                account.Version,
                validation.Lifecycle,
                validation.Health,
                validation.ProviderAccountId,
                validation.IdentityHint,
                validation.Permissions,
                validation.ProviderScopes,
                validation.CapabilityBindings,
                validation.LastSuccessfulUse ?? DateTimeOffset.UtcNow,
                token);
        }
        catch (PluginModuleException exception)
        {
            throw new ArgumentException(exception.ErrorCode, nameof(account), exception);
        }
    }

    private static async Task<ProductBoundary> Boundary(
        HttpContext context,
        ITokenValidator validator,
        TesseraConfig config,
        IServiceProvider services,
        CancellationToken token
    )
    {
        var user = await PortalEndpoints.ResolveEndUserAsync(context, validator, config);
        if (user?.CanonicalPrincipalId is null || string.IsNullOrWhiteSpace(user.TenantId))
            return new(Error: Problem(401, "unauthenticated"));
        var store = services.GetService<SqliteKernelStore>();
        if (store is null)
            return new(Error: Problem(503, "product_storage_unavailable"));
        var principal = PrincipalRef.Create(
            user.Issuer,
            user.TenantId,
            user.Subject,
            user.PreferredUsername,
            DateTimeOffset.UtcNow
        );
        if (!TryCursor(context, principal.PrincipalId, out var offset))
            return new(Error: Problem(400, "invalid_cursor"));
        context.Items["r2.cursor.offset"] = offset;
        await store.AddAsync(principal, token);
        return new(store, principal.PrincipalId);
    }

    private static IResult Problem(int status, string code) =>
        Results.Problem(
            statusCode: status,
            title: code,
            extensions: new Dictionary<string, object?> { { "code", code } }
        );

    private sealed record ProductBoundary(
        SqliteKernelStore? Store = null,
        string? Owner = null,
        IResult? Error = null
    );

    internal sealed record ChatToolContext(
        IReadOnlyList<object> Definitions,
        IReadOnlyList<ProjectedModelTool> PluginTools
    );

    internal sealed record ChatToolOutcome(ModelToolResult Result, ChatMessagePart Part);

    internal sealed record JobToolContext(
        IReadOnlyList<object> Definitions,
        IReadOnlyList<ProjectedModelTool> PluginTools
    );

    internal sealed record JobToolOutcome(
        ModelToolResult Result,
        bool WaitingForApproval,
        string? ErrorCode
    );

    private sealed record CreateConversationRequest(string? Title, string? ModelProfileId);

    private sealed record ConversationGrantsRequest(
        string[] AccountGrants,
        CapabilityGrantRequest[] CapabilityGrants,
        long ExpectedVersion
    );

    private sealed record UpdateConversationRequest(
        string? Title,
        string? State,
        long ExpectedVersion
    );

    private sealed record SendMessageRequest(string Text, string? ModelProfileId);

    private sealed record RetryMessageRequest(string MessageId);

    private sealed record StopExecutionRequest(string ExecutionId);

    private sealed record CreateAccountRequest(
        string PluginId,
        string DisplayName,
        JsonElement NonSecretConfig,
        string SecretInput
    );

    private sealed record MemoryRequest(
        string SubjectKey,
        string Predicate,
        string Value,
        string? SourceMessageId
    );

    private sealed record MemoryCorrectionRequest(string Value, string? SourceMessageId);

    private sealed record VersionRequest(long ExpectedVersion);

    private sealed class ApprovalRequest
    {
        public long ExpectedVersion { get; init; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; init; }
    }

    private sealed record PluginConfigurationRequest(JsonElement Values, long ExpectedVersion);

    private sealed record CapabilityGrantRequest(string Id, string Version);

    private sealed record CreateJobRequest(
        string Name,
        string Instruction,
        string DesiredState,
        string? ModelProfileId,
        JobSchedule Schedule,
        object? ContextPolicy,
        string[]? AccountGrants,
        CapabilityGrantRequest[]? CapabilityGrants,
        string[]? SideEffectGrants
    );

    private sealed record UpdateJobRequest(
        string? Name,
        string? Instruction,
        string? DesiredState,
        string? ModelProfileId,
        JobSchedule? Schedule,
        JsonElement? ContextPolicy,
        string[]? AccountGrants,
        CapabilityGrantRequest[]? CapabilityGrants,
        string[]? SideEffectGrants,
        long ExpectedVersion
    );

    private sealed record CreateModelProfileRequest(
        string AccountId,
        string AdapterKind,
        string Endpoint,
        string Model,
        int ContextLimit
    );

    private sealed record UpdateSettingsRequest(
        string? DefaultChatModelProfileId,
        string? DefaultLightweightModelProfileId,
        string? Timezone,
        JsonElement? ApprovalDefaults,
        JsonElement? MemoryControls,
        long ExpectedVersion
    );

    private sealed record ActionProposalRequest(
        string CapabilityId,
        string CapabilityVersion,
        string PluginId,
        string PluginVersion,
        string? AccountId,
        string Target,
        JsonElement Input,
        string? ConversationId,
        string? MessageId,
        string? JobId,
        string? JobRunId
    );

    private sealed record ReadCapabilityRequest(
        string CapabilityId,
        string CapabilityVersion,
        string PluginId,
        string PluginVersion,
        string? AccountId,
        string Target,
        JsonElement Input,
        string? ConversationId,
        string? MessageId
    );

    private sealed record ActivityItem(
        string Id,
        string Kind,
        DateTimeOffset OccurredAt,
        string Summary,
        string? State,
        string ResourceType,
        string ResourceId,
        IReadOnlyList<string> EvidenceRefs
    );

    internal sealed class ModelCapability(
        IHttpTransport transport,
        ModelProfile profile,
        string accessToken,
        Func<string, CancellationToken, ValueTask>? onTextDelta = null
    ) : ICapability
    {
        public CapabilityDescriptor Descriptor { get; } =
            CapabilityDescriptor.Create(
                "model.chat.complete",
                "1",
                "OpenAI-compatible Chat completion",
                "{}",
                "{}",
                SideEffectClass.ReadOnly,
                [],
                [SensitivityClass.Public, SensitivityClass.Internal, SensitivityClass.Confidential],
                IdempotencySupport.Keyed,
                VerificationSupport.None
            );

        public async ValueTask<CapabilityResult> InvokeAsync(
            CapabilityInvocation invocation,
            CancellationToken cancellationToken = default
        )
        {
            var prompt = invocation.Input.GetProperty("prompt").GetString() ?? string.Empty;
            var adapter = new OpenAiCompatibleAdapter(transport);
            var local = profile.AdapterKind.EndsWith("local", StringComparison.Ordinal);
            var trustedInternal =
                local
                && Uri.TryCreate(profile.Endpoint, UriKind.Absolute, out var endpoint)
                && !endpoint.IsLoopback;
            ModelTurnResult result;
            if (invocation.Input.TryGetProperty("assistantMessage", out var assistant))
            {
                var toolResults = invocation
                    .Input.GetProperty("toolResults")
                    .EnumerateArray()
                    .Select(item => new ModelToolResult(
                        item.GetProperty("callId").GetString()!,
                        item.GetProperty("outputJson").GetString()!
                    ))
                    .ToArray();
                result =
                    onTextDelta is not null
                    && profile.SupportsStreaming
                    && transport is IStreamingHttpTransport
                        ? trustedInternal
                            ? await adapter.StreamContinuationTrustedInternalAsync(
                                profile.Endpoint,
                                accessToken,
                                profile.Model,
                                prompt,
                                assistant,
                                toolResults,
                                onTextDelta,
                                cancellationToken
                            )
                            : await adapter.StreamContinuationAsync(
                                profile.Endpoint,
                                accessToken,
                                profile.Model,
                                prompt,
                                local,
                                assistant,
                                toolResults,
                                onTextDelta,
                                cancellationToken
                            )
                        : trustedInternal
                            ? await adapter.ContinueTurnTrustedInternalAsync(
                                profile.Endpoint,
                                accessToken,
                                profile.Model,
                                prompt,
                                assistant,
                                toolResults,
                                cancellationToken
                            )
                            : await adapter.ContinueTurnAsync(
                                profile.Endpoint,
                                accessToken,
                                profile.Model,
                                prompt,
                                local,
                                assistant,
                                toolResults,
                                cancellationToken
                            );
            }
            else
            {
                var tools = new List<ModelToolDefinition>();
                if (invocation.Input.TryGetProperty("tools", out var values))
                    foreach (var item in values.EnumerateArray())
                        tools.Add(
                            new(
                                item.GetProperty("name").GetString()!,
                                item.GetProperty("description").GetString()!,
                                item.GetProperty("parameters").Clone()
                            )
                        );
                result =
                    onTextDelta is not null
                    && profile.SupportsStreaming
                    && transport is IStreamingHttpTransport
                        ? trustedInternal
                            ? await adapter.StreamTurnTrustedInternalAsync(
                                profile.Endpoint,
                                accessToken,
                                profile.Model,
                                prompt,
                                tools,
                                onTextDelta,
                                cancellationToken
                            )
                            : await adapter.StreamTurnAsync(
                                profile.Endpoint,
                                accessToken,
                                profile.Model,
                                prompt,
                                local,
                                tools,
                                onTextDelta,
                                cancellationToken
                            )
                        : trustedInternal
                            ? await adapter.CompleteTurnTrustedInternalAsync(
                                profile.Endpoint,
                                accessToken,
                                profile.Model,
                                prompt,
                                tools,
                                cancellationToken
                            )
                            : await adapter.CompleteTurnAsync(
                                profile.Endpoint,
                                accessToken,
                                profile.Model,
                                prompt,
                                local,
                                tools,
                                cancellationToken
                            );
            }
            return result.Succeeded
                ? new(
                    CapabilityOutcome.Succeeded,
                    JsonSerializer.SerializeToElement(
                        new
                        {
                            text = result.Text,
                            toolCalls = result
                                .ToolCalls.Select(call => new
                                {
                                    id = call.Id,
                                    name = call.Name,
                                    arguments = call.Arguments,
                                })
                                .ToArray(),
                            assistantMessage = result.AssistantMessage,
                        }
                    ),
                    null,
                    null,
                    null
                )
                : new(
                    CapabilityOutcome.Failed,
                    JsonSerializer.SerializeToElement(new { }),
                    null,
                    null,
                    result.ErrorCode
                );
        }
    }

    private sealed class LocalTimeCapability : ICapability
    {
        public CapabilityDescriptor Descriptor { get; } =
            CapabilityDescriptor.Create(
                "local.time",
                "1",
                "Current date and time",
                "{}",
                "{}",
                SideEffectClass.ReadOnly,
                [],
                [SensitivityClass.Public, SensitivityClass.Internal],
                IdempotencySupport.Keyed,
                VerificationSupport.None
            );

        public ValueTask<CapabilityResult> InvokeAsync(
            CapabilityInvocation invocation,
            CancellationToken cancellationToken = default
        )
        {
            var zone = invocation.Input.TryGetProperty("timeZone", out var value)
                ? value.GetString() ?? "UTC"
                : "UTC";
            try
            {
                return ValueTask.FromResult(
                    new CapabilityResult(
                        CapabilityOutcome.Succeeded,
                        JsonSerializer.SerializeToElement(CurrentDateTime(zone)),
                        null,
                        null,
                        null
                    )
                );
            }
            catch (TimeZoneNotFoundException)
            {
                return ValueTask.FromResult(
                    new CapabilityResult(
                        CapabilityOutcome.Failed,
                        JsonSerializer.SerializeToElement(new { }),
                        null,
                        null,
                        "invalid_timezone"
                    )
                );
            }
        }

        private static object CurrentDateTime(string zone)
        {
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(zone);
            var value = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
            return new
            {
                timeZone = zone,
                localDateTime = value.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                utcOffset = value.Offset.ToString(),
            };
        }
    }

    private sealed class MemoryRememberCapability(SqliteKernelStore store) : ICapability
    {
        public CapabilityDescriptor Descriptor { get; } =
            CapabilityDescriptor.Create(
                "local.memory.remember",
                "1",
                "Remember explicit user-authored state",
                "{}",
                "{}",
                SideEffectClass.LocalReversible,
                [],
                [SensitivityClass.Confidential],
                IdempotencySupport.Keyed,
                VerificationSupport.None
            );

        public async ValueTask<CapabilityResult> InvokeAsync(
            CapabilityInvocation invocation,
            CancellationToken cancellationToken = default
        )
        {
            try
            {
                var assertion = await new R2MemoryService(store, store).RememberAsync(
                    invocation.OwnerPrincipalId,
                    RequiredToolText(invocation.Input, "subjectKey", 256),
                    RequiredToolText(invocation.Input, "predicate", 256),
                    RequiredToolText(invocation.Input, "value", 4096),
                    invocation.TaskOrWorkflowId,
                    DateTimeOffset.UtcNow,
                    cancellationToken
                );
                return new(
                    CapabilityOutcome.Succeeded,
                    JsonSerializer.SerializeToElement(
                        new
                        {
                            assertionId = assertion.AssertionId,
                            value = assertion.Value,
                            evidenceRefs = assertion.EvidenceRefs,
                        }
                    ),
                    null,
                    null,
                    null
                );
            }
            catch (ArgumentException)
            {
                return new(
                    CapabilityOutcome.Failed,
                    JsonSerializer.SerializeToElement(new { }),
                    null,
                    null,
                    "invalid_memory"
                );
            }
        }
    }

    private sealed class MemoryCorrectCapability(SqliteKernelStore store) : ICapability
    {
        public CapabilityDescriptor Descriptor { get; } =
            CapabilityDescriptor.Create(
                "local.memory.correct",
                "1",
                "Correct explicit user-authored state",
                "{}",
                "{}",
                SideEffectClass.LocalReversible,
                [],
                [SensitivityClass.Confidential],
                IdempotencySupport.Keyed,
                VerificationSupport.None
            );

        public async ValueTask<CapabilityResult> InvokeAsync(
            CapabilityInvocation invocation,
            CancellationToken cancellationToken = default
        )
        {
            try
            {
                var assertion = await new R2MemoryService(store, store).CorrectAsync(
                    invocation.OwnerPrincipalId,
                    RequiredToolText(invocation.Input, "assertionId", 256),
                    RequiredToolText(invocation.Input, "value", 4096),
                    invocation.TaskOrWorkflowId,
                    DateTimeOffset.UtcNow,
                    cancellationToken
                );
                return new(
                    CapabilityOutcome.Succeeded,
                    JsonSerializer.SerializeToElement(
                        new
                        {
                            assertionId = assertion.AssertionId,
                            value = assertion.Value,
                            evidenceRefs = assertion.EvidenceRefs,
                        }
                    ),
                    null,
                    null,
                    null
                );
            }
            catch (Exception exception)
                when (exception is ArgumentException or KeyNotFoundException)
            {
                return new(
                    CapabilityOutcome.Failed,
                    JsonSerializer.SerializeToElement(new { }),
                    null,
                    null,
                    "invalid_memory"
                );
            }
        }
    }

    private sealed class MemoryWhyCapability(SqliteKernelStore store) : ICapability
    {
        public CapabilityDescriptor Descriptor { get; } =
            CapabilityDescriptor.Create(
                "local.memory.why",
                "1",
                "Explain durable memory evidence and history",
                "{}",
                "{}",
                SideEffectClass.ReadOnly,
                [],
                [SensitivityClass.Confidential],
                IdempotencySupport.Keyed,
                VerificationSupport.None
            );

        public async ValueTask<CapabilityResult> InvokeAsync(
            CapabilityInvocation invocation,
            CancellationToken cancellationToken = default
        )
        {
            try
            {
                var why = await new R2MemoryService(store, store).WhyAsync(
                    invocation.OwnerPrincipalId,
                    RequiredToolText(invocation.Input, "assertionId", 256),
                    cancellationToken
                );
                return new(
                    CapabilityOutcome.Succeeded,
                    JsonSerializer.SerializeToElement(why),
                    null,
                    null,
                    null
                );
            }
            catch (KeyNotFoundException)
            {
                return new(
                    CapabilityOutcome.Failed,
                    JsonSerializer.SerializeToElement(new { }),
                    null,
                    null,
                    "memory_not_found"
                );
            }
        }
    }

}
