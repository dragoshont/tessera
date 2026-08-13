using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Xunit;

namespace Tessera.Persistence.Sqlite.Tests;

public sealed class RealtimeVoicePersistenceTests
{
    [Fact]
    public async Task V15_fixture_migrates_additively_to_v16_without_media_or_secret_columns()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync(15);
        var principal = KernelTestData.Principal();
        await store.AddAsync(principal);
        await store.AddConversationAsync(new(principal.PrincipalId, "conversation-1", "Existing", "ACTIVE", null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1));

        var restarted = database.CreateStore();
        await restarted.InitializeAsync(16);

        Assert.Equal(16, (await restarted.GetAppliedMigrationVersionsAsync())[^1]);
        Assert.NotNull(await restarted.GetConversationAsync(principal.PrincipalId, "conversation-1"));
        await using var connection = new SqliteConnection($"Data Source={database.Path};Mode=ReadOnly");
        await connection.OpenAsync();
        var names = new List<string>();
        foreach (var table in new[] { "realtime_session_receipts", "realtime_session_tools", "realtime_turn_receipts", "realtime_tool_bindings" })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info('{table}');";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) names.Add(reader.GetString(1));
        }
        Assert.DoesNotContain(names, name => new[] { "sdp", "audio", "token", "secret", "provider_body", "location", "candidate" }
            .Any(forbidden => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Expired_negotiation_is_fenced_and_never_resumed()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var (owner, conversation) = await SeedConversationAsync(store);
        var receipt = Session(owner, conversation, "session-stale", "attempt-stale", DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert.True(await store.BeginRealtimeNegotiationAsync(receipt, []));

        Assert.Equal(1, await store.FenceExpiredRealtimeNegotiationsAsync(DateTimeOffset.UtcNow));

        var fenced = await store.GetRealtimeSessionAsync(owner, receipt.SessionId);
        Assert.Equal("FAILED", fenced!.State);
        Assert.Equal("realtime_negotiation_outcome_unknown", fenced.FailureCode);
        Assert.False(await store.CompleteRealtimeNegotiationAsync(owner, receipt.SessionId,
            receipt.NegotiationGeneration, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(10)));
    }

    [Fact]
    public async Task Duplicate_provider_item_rolls_back_the_entire_second_turn_and_owner_scope_is_exact()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var (owner, conversation) = await SeedConversationAsync(store);
        var session = Session(owner, conversation, "session-1", "attempt-1", DateTimeOffset.UtcNow.AddSeconds(30));
        Assert.True(await store.BeginRealtimeNegotiationAsync(session, []));
        Assert.True(await store.CompleteRealtimeNegotiationAsync(owner, session.SessionId,
            session.NegotiationGeneration, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(10)));
        var first = Turn(owner, conversation, session.SessionId, "turn-1", "provider-input", "key-1");
        Assert.True(await store.SaveRealtimeTurnAsync(first));

        var second = Turn(owner, conversation, session.SessionId, "turn-2", "provider-input", "key-2");
        await Assert.ThrowsAsync<SqliteException>(() => store.SaveRealtimeTurnAsync(second));

        var messages = await store.ListMessagesAsync(owner, conversation);
        Assert.Equal(2, messages.Count);
        Assert.DoesNotContain(messages, message => message.MessageId == second.UserMessage.MessageId);
        Assert.Null(await store.GetRealtimeTurnAsync(owner, session.SessionId, "turn-2"));
        Assert.Null(await store.GetRealtimeSessionAsync("other-owner", session.SessionId));
    }

    [Fact]
    public async Task Session_tools_and_tool_bindings_are_sorted_canonical_and_atomically_idempotent()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var (owner, conversation) = await SeedConversationAsync(store);
        var session = Session(owner, conversation, "session-tools", "attempt-tools", DateTimeOffset.UtcNow.AddSeconds(30));
        var tools = new[]
        {
            new RealtimeSessionTool(owner, session.SessionId, "z_tool", "local", "1.0.0",
                "local.time", "1", null, "schema-z", "ReadOnly"),
            new RealtimeSessionTool(owner, session.SessionId, "a_tool", "local", "1.0.0",
                "local.time", "1", null, "schema-a", "ReadOnly"),
        };
        Assert.True(await store.BeginRealtimeNegotiationAsync(session, tools));
        Assert.Equal(["a_tool", "z_tool"],
            (await store.ListRealtimeSessionToolsAsync(owner, session.SessionId))
                .Select(item => item.ExposedName).ToArray());
        Assert.True(await store.CompleteRealtimeNegotiationAsync(owner, session.SessionId,
            session.NegotiationGeneration, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(10)));

        var now = DateTimeOffset.UtcNow;
        var requested = new RealtimeToolBinding(owner, session.SessionId, "client-call", null, null, null,
            "REQUESTED", now, now, 1);
        var reservation = new RealtimeToolCallReservation(requested, "tool-key", RealtimeVoiceServiceHash("tool-body"));
        Assert.True(await store.BeginRealtimeToolCallAsync(reservation));
        Assert.False(await store.BeginRealtimeToolCallAsync(reservation));
        Assert.Equal("REQUESTED", (await store.GetRealtimeToolBindingAsync(
            owner, session.SessionId, requested.ClientCallId))!.State);
        var idempotency = await store.GetIdempotencyReceiptAsync(owner, "realtime-tool", "tool-key");
        Assert.Equal(202, idempotency!.ResponseStatus);

        var canonicalCallId = $"realtime:{session.SessionId}:{requested.ClientCallId}";
        using var input = JsonDocument.Parse("{}");
        var executionRequest = new ExecutionRequest(owner, canonicalCallId, "local.time", "1", "local", "1.0.0",
            null, "UTC", RealtimeVoiceServiceHash("UTC"), input.RootElement.Clone(), canonicalCallId, conversation);
        await store.BeginCapabilityCallAsync(executionRequest, now);
        await store.CompleteCapabilityCallAsync(executionRequest,
            new(CapabilityOutcome.Succeeded, JsonSerializer.SerializeToElement(new { value = "safe" }), null, null, null), now);
        var completed = requested with
        {
            CapabilityCallId = canonicalCallId,
            CapabilityResultId = $"{canonicalCallId}:result",
            State = "COMPLETED",
            UpdatedAt = now.AddSeconds(1),
        };
        Assert.True(await store.CompleteRealtimeToolCallAsync(
            reservation, completed, 200, "{\"state\":\"COMPLETED\"}"));
        var saved = await store.GetRealtimeToolBindingAsync(owner, session.SessionId, requested.ClientCallId);
        Assert.Equal("COMPLETED", saved!.State);
        Assert.Equal(canonicalCallId, saved.CapabilityCallId);
        Assert.Equal($"{canonicalCallId}:result", saved.CapabilityResultId);
        Assert.Equal(2, saved.Version);
        Assert.Equal(200, (await store.GetIdempotencyReceiptAsync(owner, "realtime-tool", "tool-key"))!.ResponseStatus);

        var conflicting = new RealtimeToolCallReservation(
            requested with { ClientCallId = "other-client-call" }, "tool-key", RealtimeVoiceServiceHash("other-body"));
        Assert.False(await store.BeginRealtimeToolCallAsync(conflicting));
        Assert.Null(await store.GetRealtimeToolBindingAsync(owner, session.SessionId, "other-client-call"));
    }

    private static async Task<(string Owner, string Conversation)> SeedConversationAsync(SqliteKernelStore store)
    {
        var principal = KernelTestData.Principal(subject: Guid.NewGuid().ToString("N"));
        await store.AddAsync(principal);
        var conversation = Guid.NewGuid().ToString("N");
        await store.AddConversationAsync(new(principal.PrincipalId, conversation, "Voice", "ACTIVE", null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1));
        return (principal.PrincipalId, conversation);
    }

    private static RealtimeSessionReceipt Session(string owner, string conversation, string session,
        string attempt, DateTimeOffset deadline) => new(owner, session, conversation, attempt,
        RealtimeVoiceServiceHash($"key-{attempt}"), RealtimeVoiceServiceHash($"offer-{attempt}"), "NEGOTIATING", 1,
        deadline, "gpt-realtime-2.1", "2026-07-07",
        "tessera-realtime-21", null, DateTimeOffset.UtcNow.AddMinutes(15), null, null, null, 1);

    private static RealtimeTurnWrite Turn(string owner, string conversation, string session, string turn,
        string inputItem, string key)
    {
        var now = DateTimeOffset.UtcNow;
        var user = new ChatMessage(owner, $"user-{turn}", conversation, "USER", "PERSISTED", null,
            [new($"user-part-{turn}", 1, "TEXT", "User text")], now, now, 1);
        var assistant = new ChatMessage(owner, $"assistant-{turn}", conversation, "ASSISTANT", "COMPLETED", null,
            [new($"assistant-part-{turn}", 1, "TEXT", "Assistant text")], now, now, 1);
        var receipt = new RealtimeTurnReceipt(owner, session, turn, inputItem, $"output-{turn}",
            user.MessageId, assistant.MessageId, "COMPLETED", now);
        return new(owner, conversation, session, key, RealtimeVoiceServiceHash(turn), receipt, user, assistant, []);
    }

    private static string RealtimeVoiceServiceHash(string value) => Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}