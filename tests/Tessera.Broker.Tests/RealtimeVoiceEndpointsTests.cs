using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Tessera.Core.Stores;
using Tessera.Persistence.Sqlite;
using Xunit;

namespace Tessera.Broker.Tests;

public sealed class RealtimeVoiceEndpointsTests : IAsyncLifetime
{
    private const string Owner = "voice-owner@example.com";
    private const string Other = "other-owner@example.com";
    private const string Offer = "v=0\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\n";
    private const string Answer = "v=0\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\na=recvonly\r\n";
    private const string DevHeader = "X-Tessera-Dev-Principal";

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private FakeRealtimeTransport _transport = null!;
    private string _directory = null!;
    private string _databasePath = null!;

    public async Task InitializeAsync()
    {
        var port = FreePort();
        _directory = Directory.CreateTempSubdirectory("tessera-realtime-test").FullName;
        var configPath = Path.Combine(_directory, "tessera.json");
        await File.WriteAllTextAsync(configPath, $$"""
            {
              "server": { "host": "127.0.0.1", "port": {{port}} },
              "identity": { "mode": "dev", "trustDomain": "tessera.local" },
              "policy": { "default": "deny" },
              "audit": { "enabled": false },
              "realtimeVoice": {
                "enabled": true,
                "endpoint": "https://foundry.example",
                "credentialRef": "voice-standing",
                "authenticationMode": "api-key",
                "voice": "marin",
                "transcriptionModel": "whisper-1",
                "maxSessionSeconds": 900,
                "ownerSessionLimit": 1,
                "globalSessionLimit": 4
              }
            }
            """);
        var policyPath = Path.Combine(_directory, "grants.json");
        await File.WriteAllTextAsync(policyPath, "{ \"grants\": [], \"bindings\": [], \"recipes\": [] }");
        var custody = new InMemoryCredentialStore();
        await custody.PutBundleAsync("voice-standing", new CredentialBundle(AccessToken: "standing-canary"));
        _transport = new FakeRealtimeTransport();
        _databasePath = Path.Combine(_directory, "product.db");
        _app = await BrokerHost.BuildAppAsync(new BrokerHostOptions
        {
            ConfigPath = configPath,
            PolicyPath = policyPath,
            ProductDatabasePath = _databasePath,
            StoreOverride = custody,
            RealtimeTransportOverride = _transport,
            PluginRoot = Path.Combine(_directory, "plugins"),
        });
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        for (var attempt = 0; attempt < 100 && _transport.SecretRequests == 0; attempt++)
            await Task.Delay(10);
        Assert.Equal(1, _transport.SecretRequests);
    }

    [Fact]
    public async Task Cached_status_and_negotiation_are_private_fixed_single_flight_and_owner_scoped()
    {
        using var unauthenticated = await _client.GetAsync("/api/v1/realtime-voice/status");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        var probeCount = _transport.SecretRequests;
        using var statusResponse = await SendAsync(Owner, HttpMethod.Get, "/api/v1/realtime-voice/status");
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        var status = await Json(statusResponse);
        Assert.Equal("READY", status.GetProperty("state").GetString());
        Assert.True(status.GetProperty("supportsTools").GetBoolean());
        Assert.Equal(900, status.GetProperty("maxSessionSeconds").GetInt32());
        var probeSession = Assert.Single(_transport.Sessions);
        Assert.Null(probeSession.Instructions);
        Assert.Empty(probeSession.Tools ?? []);
        using var secondStatus = await SendAsync(Owner, HttpMethod.Get, "/api/v1/realtime-voice/status");
        Assert.Equal(probeCount, _transport.SecretRequests);

        var conversationId = await CreateConversationAsync(Owner, "voice-conversation");
        var ownerId = PrincipalRef.Create("https://dev.tessera.local", "dev", Owner, Owner, DateTimeOffset.UtcNow).PrincipalId;
        var historyCanary = "private-history-canary";
        var now = DateTimeOffset.UtcNow;
        await _app.Services.GetRequiredService<SqliteKernelStore>().AddMessageAsync(new(
            ownerId, "history-message", conversationId, "USER", "PERSISTED", null,
            [new("history-part", 1, "TEXT", historyCanary)], now, now, 1));
        using var unknownField = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/realtime-sessions",
            new { clientAttemptId = "attempt-unknown", offerSdp = Offer, model = "client-selected" }, "voice-unknown");
        Assert.Equal(HttpStatusCode.BadRequest, unknownField.StatusCode);
        Assert.Equal(probeCount, _transport.SecretRequests);

        using var invalidOffer = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/realtime-sessions",
            new { clientAttemptId = "attempt-invalid", offerSdp = "v=0\r\nm=video 9 RTP/AVP 96\r\n" }, "voice-invalid");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalidOffer.StatusCode);
        Assert.Equal("realtime_offer_invalid", (await Json(invalidOffer)).GetProperty("code").GetString());

        _transport.BlockSdp();
        var firstNegotiationTask = SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/realtime-sessions",
            new { clientAttemptId = "attempt-1", offerSdp = Offer }, "voice-key-1");
        for (var attempt = 0; attempt < 100 && _transport.SdpRequests == 0; attempt++) await Task.Delay(10);
        var joinedNegotiationTask = SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/realtime-sessions",
            new { clientAttemptId = "attempt-1", offerSdp = Offer }, "voice-key-1");
        await Task.Delay(25);
        Assert.Equal(1, _transport.SdpRequests);
        _transport.ReleaseSdp();
        using var negotiated = await firstNegotiationTask;
        using var joinedNegotiation = await joinedNegotiationTask;
        Assert.Equal(HttpStatusCode.Created, negotiated.StatusCode);
        var negotiationBody = await negotiated.Content.ReadAsStringAsync();
        Assert.Equal(negotiationBody, await joinedNegotiation.Content.ReadAsStringAsync());
        using var negotiationDocument = JsonDocument.Parse(negotiationBody);
        var negotiation = negotiationDocument.RootElement;
        var sessionId = negotiation.GetProperty("sessionId").GetString()!;
        Assert.Equal(Answer, negotiation.GetProperty("answerSdp").GetString());
        Assert.Equal(5, negotiation.EnumerateObject().Count());
        Assert.DoesNotContain("standing-canary", negotiationBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ephemeral-canary", negotiationBody, StringComparison.Ordinal);
        Assert.DoesNotContain("foundry.example", negotiationBody, StringComparison.Ordinal);
        Assert.DoesNotContain("gpt-realtime", negotiationBody, StringComparison.Ordinal);
        Assert.DoesNotContain(historyCanary, negotiationBody, StringComparison.Ordinal);
        Assert.Equal(new Uri("https://foundry.example/"), _transport.LastEndpoint);
        Assert.Equal("standing-canary", _transport.LastStandingCredential);
        Assert.Equal("ephemeral-canary", _transport.LastConsumedSecret);
        Assert.Equal(Offer, _transport.LastOffer);
        Assert.Contains(historyCanary, _transport.LastSession?.Instructions, StringComparison.Ordinal);
        Assert.Equal(["correct_memory", "current_time", "remember_memory", "why_memory"],
            (_transport.LastSession?.Tools ?? [])
                .Select(item => item.GetProperty("name").GetString()!).ToArray());
        Assert.Equal("marin", _transport.LastSession?.Voice);
        Assert.Equal("whisper-1", _transport.LastSession?.TranscriptionModel);
        Assert.Equal(1, _transport.SdpRequests);

        using var replay = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/realtime-sessions",
            new { clientAttemptId = "attempt-1", offerSdp = Offer }, "voice-key-1");
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(negotiationBody, await replay.Content.ReadAsStringAsync());
        Assert.Equal(1, _transport.SdpRequests);

        using var conflict = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/realtime-sessions",
            new { clientAttemptId = "attempt-1", offerSdp = Offer + "a=sendrecv\r\n" }, "voice-key-1");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("idempotency_conflict", (await Json(conflict)).GetProperty("code").GetString());

        using var otherTurn = await SendJsonAsync(Other, HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/realtime-sessions/{sessionId}/turns",
            Turn("turn-other"), "turn-other-key");
        Assert.Equal(HttpStatusCode.NotFound, otherTurn.StatusCode);
    }

    [Fact]
    public async Task Transcript_is_atomic_canonical_and_idempotent_tools_fail_closed_and_end_is_metadata_only()
    {
        var conversationId = await CreateConversationAsync(Owner, "voice-turn-conversation");
        using var negotiated = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/realtime-sessions",
            new { clientAttemptId = "turn-attempt", offerSdp = Offer }, "turn-negotiation-key");
        var sessionId = (await Json(negotiated)).GetProperty("sessionId").GetString()!;

        using var savedResponse = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/realtime-sessions/{sessionId}/turns",
            Turn("turn-1"), "turn-key-1");
        Assert.Equal(HttpStatusCode.Created, savedResponse.StatusCode);
        var savedBody = await savedResponse.Content.ReadAsStringAsync();
        using var savedDocument = JsonDocument.Parse(savedBody);
        Assert.False(savedDocument.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal("PERSISTED", savedDocument.RootElement.GetProperty("userMessage").GetProperty("status").GetString());
        Assert.Equal("STOPPED", savedDocument.RootElement.GetProperty("assistantMessage").GetProperty("status").GetString());

        var ownerId = PrincipalRef.Create("https://dev.tessera.local", "dev", Owner, Owner, DateTimeOffset.UtcNow).PrincipalId;
        var messages = await _app.Services.GetRequiredService<SqliteKernelStore>().ListMessagesAsync(ownerId, conversationId);
        Assert.Equal(2, messages.Count);
        Assert.All(messages, message => Assert.Single(message.Parts));
        Assert.DoesNotContain(messages.SelectMany(message => message.Parts), part => part.EvidenceRefs?.Count > 0 || part.ActionId is not null);

        using var replayResponse = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/realtime-sessions/{sessionId}/turns",
            Turn("turn-1"), "turn-key-1");
        Assert.Equal(HttpStatusCode.Created, replayResponse.StatusCode);
        Assert.True((await Json(replayResponse)).GetProperty("replayed").GetBoolean());
        Assert.Equal(2, (await _app.Services.GetRequiredService<SqliteKernelStore>().ListMessagesAsync(ownerId, conversationId)).Count);

        using var changedReplay = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/realtime-sessions/{sessionId}/turns",
            Turn("turn-1", "changed transcript"), "turn-key-1");
        Assert.Equal(HttpStatusCode.Conflict, changedReplay.StatusCode);
        Assert.Equal("idempotency_conflict", (await Json(changedReplay)).GetProperty("code").GetString());

        using var blank = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/realtime-sessions/{sessionId}/turns",
            Turn("turn-blank", "  "), "turn-blank-key");
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);

        using var control = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/realtime-sessions/{sessionId}/turns",
            Turn("turn-control", "bad\u0001text"), "turn-control-key");
        Assert.Equal(HttpStatusCode.BadRequest, control.StatusCode);

        using var oversized = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/realtime-sessions/{sessionId}/turns",
            Turn("turn-oversized", new string('x', 32 * 1024 + 1)), "turn-oversized-key");
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);

        using var tool = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/realtime-sessions/{sessionId}/tool-calls",
            new { clientCallId = "call-1", name = "reviewed.local.read", arguments = new { } }, "tool-key-1");
        Assert.Equal(HttpStatusCode.OK, tool.StatusCode);
        var unavailableTool = await Json(tool);
        Assert.Equal("FAILED", unavailableTool.GetProperty("state").GetString());
        Assert.Equal("tool_not_advertised", unavailableTool.GetProperty("errorCode").GetString());
        using var invalidTool = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/realtime-sessions/{sessionId}/tool-calls",
            new { clientCallId = "call-invalid", name = "reviewed.local.read", arguments = "not-an-object" }, "tool-invalid-key");
        Assert.Equal(HttpStatusCode.BadRequest, invalidTool.StatusCode);

        using var ended = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/realtime-sessions/{sessionId}/end",
            new { reason = "USER_ENDED" }, "end-key-1");
        Assert.Equal(HttpStatusCode.OK, ended.StatusCode);
        var endedBody = await ended.Content.ReadAsStringAsync();
        using var endReplay = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/realtime-sessions/{sessionId}/end",
            new { reason = "USER_ENDED" }, "end-key-1");
        Assert.Equal(endedBody, await endReplay.Content.ReadAsStringAsync());
        using var endConflict = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/realtime-sessions/{sessionId}/end",
            new { reason = "ERROR" }, "end-key-1");
        Assert.Equal(HttpStatusCode.Conflict, endConflict.StatusCode);
        using var lateTurn = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/realtime-sessions/{sessionId}/turns",
            Turn("turn-late"), "turn-late-key");
        Assert.Equal(HttpStatusCode.Conflict, lateTurn.StatusCode);
        Assert.Equal("realtime_session_ended", (await Json(lateTurn)).GetProperty("code").GetString());
        using var lateTool = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/realtime-sessions/{sessionId}/tool-calls",
            new { clientCallId = "call-late", name = "reviewed.local.read", arguments = new { } }, "tool-late-key");
        Assert.Equal(HttpStatusCode.Conflict, lateTool.StatusCode);
        Assert.Equal("realtime_session_ended", (await Json(lateTool)).GetProperty("code").GetString());
        Assert.Equal(2, (await _app.Services.GetRequiredService<SqliteKernelStore>()
            .ListMessagesAsync(ownerId, conversationId)).Count);
        var events = await _app.Services.GetRequiredService<SqliteKernelStore>()
            .ListExecutionEventsAsync(ownerId, sessionId, 0);
        Assert.Equal(["realtime_negotiated", "realtime_turn_saved", "realtime_ended"],
            events.Select(item => item.EventType).ToArray());
    }

    [Fact]
    public async Task Tool_relay_uses_exact_canonical_bindings_actions_replay_and_fails_closed_on_drift()
    {
        var conversationId = await CreateConversationAsync(Owner, "voice-tools-conversation");
        var ownerId = PrincipalRef.Create("https://dev.tessera.local", "dev", Owner, Owner, DateTimeOffset.UtcNow).PrincipalId;
        var store = _app.Services.GetRequiredService<SqliteKernelStore>();
        Assert.True(await store.ReplaceConversationGrantsAsync(ownerId, conversationId, 2, [],
        [
            ("local.time", "1"),
            ("local.memory.remember", "1"),
            ("local.memory.correct", "1"),
            ("local.memory.why", "1"),
        ]));
        using var negotiated = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/realtime-sessions",
            new { clientAttemptId = "tools-attempt", offerSdp = Offer }, "tools-negotiation-key");
        Assert.Equal(HttpStatusCode.Created, negotiated.StatusCode);
        var sessionId = (await Json(negotiated)).GetProperty("sessionId").GetString()!;

        var advertised = _transport.LastSession?.Tools ?? [];
        Assert.Equal(["correct_memory", "current_time", "remember_memory", "why_memory"],
            advertised.Select(item => item.GetProperty("name").GetString()!).ToArray());
        Assert.All(advertised, item => Assert.Equal("function", item.GetProperty("type").GetString()));
        var captured = await store.ListRealtimeSessionToolsAsync(ownerId, sessionId);
        Assert.Equal(advertised.Select(item => item.GetProperty("name").GetString()!),
            captured.Select(item => item.ExposedName));
        Assert.All(captured, item => Assert.Equal("local", item.PluginId));

        var readBody = new { clientCallId = "read-1", name = "current_time", arguments = new { timeZone = "UTC" } };
        using var read = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/realtime-sessions/{sessionId}/tool-calls",
            readBody, "read-key");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var readText = await read.Content.ReadAsStringAsync();
        using var readDocument = JsonDocument.Parse(readText);
        var readResult = readDocument.RootElement;
        var readReceipt = await store.GetCapabilityReceiptAsync(ownerId, $"realtime:{sessionId}:read-1");
        Assert.True(readResult.GetProperty("state").GetString() == "COMPLETED", $"{readText}\n{readReceipt}");
        Assert.Equal(JsonValueKind.Object, readResult.GetProperty("output").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(readResult.GetProperty("capabilityCallId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(readResult.GetProperty("capabilityResultId").GetString()));
        Assert.DoesNotContain("standing-canary", readText, StringComparison.Ordinal);
        Assert.DoesNotContain("ephemeral-canary", readText, StringComparison.Ordinal);
        Assert.DoesNotContain("private-history-canary", readText, StringComparison.Ordinal);

        using var exactReplay = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/realtime-sessions/{sessionId}/tool-calls",
            readBody, "read-key");
        Assert.Equal(readText, await exactReplay.Content.ReadAsStringAsync());
        using var changedReplay = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/realtime-sessions/{sessionId}/tool-calls",
            new { clientCallId = "read-1", name = "current_time", arguments = new { timeZone = "Europe/Bucharest" } },
            "read-key");
        Assert.Equal(HttpStatusCode.Conflict, changedReplay.StatusCode);
        Assert.Equal("idempotency_conflict", (await Json(changedReplay)).GetProperty("code").GetString());

        var actionBody = new
        {
            clientCallId = "action-1",
            name = "remember_memory",
            arguments = new { subjectKey = "user", predicate = "voice.assent", value = "yes, I approve" },
        };
        using var action = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/realtime-sessions/{sessionId}/tool-calls",
            actionBody, "action-key");
        Assert.Equal(HttpStatusCode.Accepted, action.StatusCode);
        var actionResult = await Json(action);
        Assert.Equal("APPROVAL_REQUIRED", actionResult.GetProperty("state").GetString());
        Assert.False(string.IsNullOrWhiteSpace(actionResult.GetProperty("actionId").GetString()));
        Assert.Equal(JsonValueKind.Null, actionResult.GetProperty("output").ValueKind);
        var actionId = actionResult.GetProperty("actionId").GetString()!;
        var proposed = await store.GetActionAsync(ownerId, actionId) ?? throw new InvalidDataException("Action missing.");
        var durable = await ((IDurableExecutionRequestRepository)store).GetAsync(ownerId, actionId)
            ?? throw new InvalidDataException("Durable request missing.");
        Assert.Equal(ActionState.Proposed, proposed.State);
        Assert.Equal(CapabilityPayloadHash.Compute(durable.Input), proposed.PayloadHash);
        Assert.Equal(durable.TargetScope, proposed.TargetScope);
        Assert.Equal(durable.PluginId, proposed.R2Binding?.PluginId);
        Assert.Equal(durable.PluginVersion, proposed.R2Binding?.PluginVersion);
        Assert.Equal(durable.ExecutionId, proposed.R2Binding?.ExecutionId);
        Assert.Equal(durable.AccountId, proposed.R2Binding?.AccountId);
        Assert.Equal(durable.TargetHash, proposed.R2Binding?.TargetHash);
        Assert.True(proposed.R2Binding?.ExpiresAt > DateTimeOffset.UtcNow);
        using var approved = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/actions/{actionId}/approve", new { expectedVersion = proposed.Version }, "voice-action-approval");
        var approvedBody = await approved.Content.ReadAsStringAsync();
        Assert.True(approved.StatusCode == HttpStatusCode.Accepted, $"{approved.StatusCode}: {approvedBody}");
        using var continued = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/realtime-sessions/{sessionId}/tool-calls",
            actionBody, "action-key");
        Assert.Equal(HttpStatusCode.OK, continued.StatusCode);
        var continuedResult = await Json(continued);
        Assert.Equal("COMPLETED", continuedResult.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Object, continuedResult.GetProperty("output").ValueKind);

        using var clientApproval = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/realtime-sessions/{sessionId}/tool-calls",
            new
            {
                clientCallId = "action-client-approved",
                name = "remember_memory",
                arguments = new { subjectKey = "user", predicate = "voice.assent", value = "yes", approved = true },
            }, "action-client-approved-key");
        Assert.Equal("FAILED", (await Json(clientApproval)).GetProperty("state").GetString());
        Assert.Equal("invalid_tool_arguments", (await Json(clientApproval)).GetProperty("errorCode").GetString());

        await ExecuteDatabaseAsync("UPDATE realtime_session_tools SET schema_hash='drifted' "
            + "WHERE session_id=$session AND exposed_name='current_time';", sessionId);
        await AssertToolFailureAsync(conversationId, sessionId, "schema-drift", "current_time",
            new { timeZone = "UTC" }, "tool_binding_changed");

        await ExecuteDatabaseAsync("PRAGMA foreign_keys=OFF; UPDATE realtime_session_tools SET account_id='revoked-account' "
            + "WHERE session_id=$session AND exposed_name='remember_memory';", sessionId);
        await AssertToolFailureAsync(conversationId, sessionId, "account-drift", "remember_memory",
            new { subjectKey = "user", predicate = "voice.account", value = "drift" }, "tool_binding_changed");

        await ExecuteDatabaseAsync("UPDATE realtime_session_tools SET plugin_id='disabled-plugin' "
            + "WHERE session_id=$session AND exposed_name='why_memory';", sessionId);
        await AssertToolFailureAsync(conversationId, sessionId, "disabled-tool", "why_memory",
            new { assertionId = "missing" }, "tool_binding_changed");

        Assert.True(await store.ReplaceConversationGrantsAsync(ownerId, conversationId, 3, [], []));
        await AssertToolFailureAsync(conversationId, sessionId, "grant-drift", "correct_memory",
            new { assertionId = "missing", value = "changed" }, "tool_binding_changed");

        var resultId = readResult.GetProperty("capabilityResultId").GetString()!;
        var outputCanary = "tool-output-secret-canary-" + new string('x', 20 * 1024);
        await using (var connection = new SqliteConnection($"Data Source={_databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE capability_results SET data_json=$data WHERE result_id=$result;";
            command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(new { outputCanary }));
            command.Parameters.AddWithValue("$result", resultId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        using var boundedReplay = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/realtime-sessions/{sessionId}/tool-calls",
            readBody, "read-key");
        var boundedText = await boundedReplay.Content.ReadAsStringAsync();
        Assert.DoesNotContain("tool-output-secret-canary", boundedText, StringComparison.Ordinal);
        Assert.True(boundedText.Length < 2048);
        Assert.Equal("tool_result_unavailable", (await Json(boundedReplay)).GetProperty("errorCode").GetString());
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private async Task<string> CreateConversationAsync(string owner, string key)
    {
        using var response = await SendJsonAsync(owner, HttpMethod.Post, "/api/v1/conversations",
            new { title = "Voice", modelProfileId = (string?)null }, key);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("conversationId").GetString()!;
    }

    private static object Turn(string id, string userTranscript = "Hello from voice") => new
    {
        clientTurnId = id,
        inputItemId = $"input-{id}",
        outputItemId = $"output-{id}",
        userTranscript,
        assistantTranscript = "Hello back",
        assistantDisposition = "INTERRUPTED",
    };

    private async Task<HttpResponseMessage> SendJsonAsync(string owner, HttpMethod method, string path, object body, string key)
    {
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add(DevHeader, owner);
        request.Headers.Add("Idempotency-Key", key);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendAsync(string owner, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(DevHeader, owner);
        return await _client.SendAsync(request);
    }

    private async Task AssertToolFailureAsync(
        string conversationId, string sessionId, string clientCallId, string name, object arguments, string errorCode)
    {
        using var response = await SendJsonAsync(Owner, HttpMethod.Post,
            $"/api/v1/conversations/{conversationId}/realtime-sessions/{sessionId}/tool-calls",
            new { clientCallId, name, arguments }, $"{clientCallId}-key");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("FAILED", body.GetProperty("state").GetString());
        Assert.Equal(errorCode, body.GetProperty("errorCode").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("output").ValueKind);
    }

    private async Task ExecuteDatabaseAsync(string sql, string sessionId)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$session", sessionId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class FakeRealtimeTransport : IRealtimeFoundryTransport
    {
        private int _secretRequests;
        private int _sdpRequests;
        public int SecretRequests => Volatile.Read(ref _secretRequests);
        public int SdpRequests => Volatile.Read(ref _sdpRequests);
        public Uri? LastEndpoint { get; private set; }
        public string? LastStandingCredential { get; private set; }
        public string? LastConsumedSecret { get; private set; }
        public string? LastOffer { get; private set; }
        public RealtimeFoundrySessionConfiguration? LastSession { get; private set; }
        public ConcurrentQueue<RealtimeFoundrySessionConfiguration> Sessions { get; } = new();
        private TaskCompletionSource? _sdpGate;

        public void BlockSdp() => _sdpGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void ReleaseSdp() => _sdpGate?.TrySetResult();

        public Task<RealtimeFoundrySecret> CreateClientSecretAsync(Uri endpoint, RealtimeFoundryCredential credential,
            RealtimeFoundrySessionConfiguration session, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _secretRequests);
            LastEndpoint = endpoint;
            LastStandingCredential = credential.Value;
            LastSession = session;
            Sessions.Enqueue(session);
            return Task.FromResult(new RealtimeFoundrySecret("ephemeral-canary", DateTimeOffset.UtcNow.AddMinutes(20)));
        }

        public async Task<string> NegotiateSdpAsync(Uri endpoint, string ephemeralSecret, string offerSdp, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sdpRequests);
            LastEndpoint = endpoint;
            LastConsumedSecret = ephemeralSecret;
            LastOffer = offerSdp;
            if (_sdpGate is not null) await _sdpGate.Task.WaitAsync(cancellationToken);
            return Answer;
        }
    }
}