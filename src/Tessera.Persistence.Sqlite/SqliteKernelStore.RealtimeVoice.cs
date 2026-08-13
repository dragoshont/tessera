using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tessera.Core.Product;

namespace Tessera.Persistence.Sqlite;

public sealed partial class SqliteKernelStore
{
    public async Task<RealtimeSessionReceipt?> GetRealtimeSessionAsync(
        string ownerPrincipalId, string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{RealtimeSessionSelect} WHERE owner_principal_id=$owner AND session_id=$value;";
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        command.Parameters.AddWithValue("$value", sessionId);
        return await ReadRealtimeSessionAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RealtimeSessionReceipt?> GetRealtimeSessionByAttemptAsync(
        string ownerPrincipalId, string clientAttemptId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{RealtimeSessionSelect} WHERE owner_principal_id=$owner AND client_attempt_id=$value;";
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        command.Parameters.AddWithValue("$value", clientAttemptId);
        return await ReadRealtimeSessionAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> BeginRealtimeNegotiationAsync(
        RealtimeSessionReceipt receipt, IReadOnlyList<RealtimeSessionTool> tools,
        CancellationToken cancellationToken = default)
    {
        EnsureOwner(receipt.OwnerPrincipalId, receipt.OwnerPrincipalId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO realtime_session_receipts(
                    owner_principal_id,session_id,conversation_id,client_attempt_id,idempotency_key_hash,
                    offer_hash,state,negotiation_generation,negotiation_deadline,provider_model_id,
                    provider_model_version,provider_deployment_ref,negotiated_at,expires_at,ended_at,
                    end_reason,failure_code,version)
                SELECT $owner,$session,$conversation,$attempt,$keyHash,$offerHash,'NEGOTIATING',$generation,
                    $deadline,$model,$modelVersion,$deployment,NULL,$expires,NULL,NULL,NULL,1
                FROM conversations
                WHERE owner_principal_id=$owner AND conversation_id=$conversation AND state='ACTIVE';
                """;
            command.Parameters.AddWithValue("$owner", receipt.OwnerPrincipalId);
            command.Parameters.AddWithValue("$session", receipt.SessionId);
            command.Parameters.AddWithValue("$conversation", receipt.ConversationId);
            command.Parameters.AddWithValue("$attempt", receipt.ClientAttemptId);
            command.Parameters.AddWithValue("$keyHash", receipt.IdempotencyKeyHash);
            command.Parameters.AddWithValue("$offerHash", receipt.OfferHash);
            command.Parameters.AddWithValue("$generation", receipt.NegotiationGeneration);
            command.Parameters.AddWithValue("$deadline", FormatTimestamp(receipt.NegotiationDeadline));
            command.Parameters.AddWithValue("$model", receipt.ProviderModelId);
            command.Parameters.AddWithValue("$modelVersion", receipt.ProviderModelVersion);
            command.Parameters.AddWithValue("$deployment", receipt.ProviderDeploymentRef);
            command.Parameters.AddWithValue("$expires", FormatTimestamp(receipt.ExpiresAt));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        foreach (var tool in tools.OrderBy(item => item.ExposedName, StringComparer.Ordinal))
        {
            EnsureOwner(receipt.OwnerPrincipalId, tool.OwnerPrincipalId);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO realtime_session_tools(owner_principal_id,session_id,exposed_name,plugin_id,
                    plugin_version,capability_id,capability_version,account_id,schema_hash,side_effect_class)
                VALUES($owner,$session,$name,$plugin,$pluginVersion,$capability,$capabilityVersion,
                    $account,$schema,$sideEffect);
                """;
            command.Parameters.AddWithValue("$owner", tool.OwnerPrincipalId);
            command.Parameters.AddWithValue("$session", tool.SessionId);
            command.Parameters.AddWithValue("$name", tool.ExposedName);
            command.Parameters.AddWithValue("$plugin", tool.PluginId);
            command.Parameters.AddWithValue("$pluginVersion", tool.PluginVersion);
            command.Parameters.AddWithValue("$capability", tool.CapabilityId);
            command.Parameters.AddWithValue("$capabilityVersion", tool.CapabilityVersion);
            command.Parameters.AddWithValue("$account", (object?)tool.AccountId ?? DBNull.Value);
            command.Parameters.AddWithValue("$schema", tool.SchemaHash);
            command.Parameters.AddWithValue("$sideEffect", tool.SideEffectClass);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<RealtimeSessionTool>> ListRealtimeSessionToolsAsync(
        string ownerPrincipalId, string sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT exposed_name,plugin_id,plugin_version,capability_id,capability_version,
                account_id,schema_hash,side_effect_class
            FROM realtime_session_tools
            WHERE owner_principal_id=$owner AND session_id=$session
            ORDER BY exposed_name;
            """;
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        command.Parameters.AddWithValue("$session", sessionId);
        var values = new List<RealtimeSessionTool>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            values.Add(new(ownerPrincipalId, sessionId, reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetString(4), ReadNullableString(reader, 5),
                reader.GetString(6), reader.GetString(7)));
        return values.AsReadOnly();
    }

    public async Task<RealtimeToolBinding?> GetRealtimeToolBindingAsync(
        string ownerPrincipalId, string sessionId, string clientCallId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT capability_call_id,capability_result_id,action_id,state,created_at,updated_at,version
            FROM realtime_tool_bindings
            WHERE owner_principal_id=$owner AND session_id=$session AND client_call_id=$call;
            """;
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$call", clientCallId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(ownerPrincipalId, sessionId, clientCallId, ReadNullableString(reader, 0),
                ReadNullableString(reader, 1), ReadNullableString(reader, 2), reader.GetString(3),
                ParseTimestamp(reader.GetString(4)), ParseTimestamp(reader.GetString(5)), reader.GetInt64(6))
            : null;
    }

    public async Task<bool> BeginRealtimeToolCallAsync(
        RealtimeToolCallReservation reservation, CancellationToken cancellationToken = default)
    {
        var binding = reservation.Binding;
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO realtime_tool_bindings(owner_principal_id,session_id,client_call_id,
                    capability_call_id,capability_result_id,action_id,state,created_at,updated_at,version)
                SELECT $owner,$session,$call,NULL,NULL,NULL,'REQUESTED',$created,$updated,1
                FROM realtime_session_receipts
                WHERE owner_principal_id=$owner AND session_id=$session
                    AND state='NEGOTIATED' AND expires_at>$created;
                """;
            command.Parameters.AddWithValue("$owner", binding.OwnerPrincipalId);
            command.Parameters.AddWithValue("$session", binding.SessionId);
            command.Parameters.AddWithValue("$call", binding.ClientCallId);
            command.Parameters.AddWithValue("$created", FormatTimestamp(binding.CreatedAt));
            command.Parameters.AddWithValue("$updated", FormatTimestamp(binding.UpdatedAt));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }
        await using (var receipt = connection.CreateCommand())
        {
            receipt.Transaction = transaction;
            receipt.CommandText = """
                INSERT OR IGNORE INTO idempotency_receipts(owner_principal_id,route_family,idempotency_key,
                    request_hash,response_status,response_body_json,resource_type,resource_id,created_at)
                VALUES($owner,'realtime-tool',$key,$hash,202,'{}','realtime_tool',$resource,$created);
                """;
            receipt.Parameters.AddWithValue("$owner", binding.OwnerPrincipalId);
            receipt.Parameters.AddWithValue("$key", reservation.IdempotencyKey);
            receipt.Parameters.AddWithValue("$hash", reservation.RequestHash);
            receipt.Parameters.AddWithValue("$resource", $"{binding.SessionId}:{binding.ClientCallId}");
            receipt.Parameters.AddWithValue("$created", FormatTimestamp(binding.CreatedAt));
            if (await receipt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> CompleteRealtimeToolCallAsync(
        RealtimeToolCallReservation reservation, RealtimeToolBinding completed,
        int responseStatus, string responseBodyJson, CancellationToken cancellationToken = default)
    {
        EnsureOwner(reservation.Binding.OwnerPrincipalId, completed.OwnerPrincipalId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE realtime_tool_bindings
                SET capability_call_id=$capabilityCall,capability_result_id=$capabilityResult,
                    action_id=$action,state=$state,updated_at=$updated,version=version+1
                WHERE owner_principal_id=$owner AND session_id=$session AND client_call_id=$call
                    AND state IN ('REQUESTED','RUNNING','APPROVAL_REQUIRED');
                """;
            command.Parameters.AddWithValue("$owner", completed.OwnerPrincipalId);
            command.Parameters.AddWithValue("$session", completed.SessionId);
            command.Parameters.AddWithValue("$call", completed.ClientCallId);
            command.Parameters.AddWithValue("$capabilityCall", (object?)completed.CapabilityCallId ?? DBNull.Value);
            command.Parameters.AddWithValue("$capabilityResult", (object?)completed.CapabilityResultId ?? DBNull.Value);
            command.Parameters.AddWithValue("$action", (object?)completed.ActionId ?? DBNull.Value);
            command.Parameters.AddWithValue("$state", completed.State);
            command.Parameters.AddWithValue("$updated", FormatTimestamp(completed.UpdatedAt));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }
        await using (var receipt = connection.CreateCommand())
        {
            receipt.Transaction = transaction;
            receipt.CommandText = """
                UPDATE idempotency_receipts
                SET response_status=$status,response_body_json=$body
                WHERE owner_principal_id=$owner AND route_family='realtime-tool'
                    AND idempotency_key=$key AND request_hash=$hash AND resource_id=$resource;
                """;
            receipt.Parameters.AddWithValue("$owner", completed.OwnerPrincipalId);
            receipt.Parameters.AddWithValue("$key", reservation.IdempotencyKey);
            receipt.Parameters.AddWithValue("$hash", reservation.RequestHash);
            receipt.Parameters.AddWithValue("$status", responseStatus);
            receipt.Parameters.AddWithValue("$body", responseBodyJson);
            receipt.Parameters.AddWithValue("$resource", $"{completed.SessionId}:{completed.ClientCallId}");
            if (await receipt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Task<bool> CompleteRealtimeNegotiationAsync(
        string ownerPrincipalId, string sessionId, long generation, DateTimeOffset negotiatedAt,
        DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
        => CompleteRealtimeNegotiationCoreAsync(ownerPrincipalId, sessionId, generation, negotiatedAt,
            expiresAt, cancellationToken);

    public Task<bool> FailRealtimeNegotiationAsync(
        string ownerPrincipalId, string sessionId, long generation, string failureCode,
        CancellationToken cancellationToken = default)
        => TransitionNegotiationAsync(ownerPrincipalId, sessionId, generation, "FAILED", failureCode,
            null, null, cancellationToken);

    public async Task<int> FenceExpiredRealtimeNegotiationsAsync(
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE realtime_session_receipts
            SET state='FAILED',failure_code='realtime_negotiation_outcome_unknown',version=version+1
            WHERE state='NEGOTIATING' AND negotiation_deadline<=$now;
            """;
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CountOpenRealtimeSessionsAsync(
        string? ownerPrincipalId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM realtime_session_receipts
            WHERE ($owner IS NULL OR owner_principal_id=$owner)
              AND ((state='NEGOTIATING' AND negotiation_deadline>$now)
                OR (state='NEGOTIATED' AND expires_at>$now));
            """;
        command.Parameters.AddWithValue("$owner", (object?)ownerPrincipalId ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<RealtimeTurnReceipt?> GetRealtimeTurnAsync(
        string ownerPrincipalId, string sessionId, string clientTurnId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT input_item_id,output_item_id,user_message_id,assistant_message_id,
                assistant_disposition,created_at
            FROM realtime_turn_receipts
            WHERE owner_principal_id=$owner AND session_id=$session AND client_turn_id=$turn;
            """;
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$turn", clientTurnId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(ownerPrincipalId, sessionId, clientTurnId, reader.GetString(0), ReadNullableString(reader, 1),
                reader.GetString(2), ReadNullableString(reader, 3), reader.GetString(4), ParseTimestamp(reader.GetString(5)))
            : null;
    }

    public async Task<bool> SaveRealtimeTurnAsync(
        RealtimeTurnWrite write, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var guard = connection.CreateCommand())
        {
            guard.Transaction = transaction;
            guard.CommandText = """
                SELECT 1 FROM realtime_session_receipts
                WHERE owner_principal_id=$owner AND session_id=$session AND conversation_id=$conversation
                  AND state='NEGOTIATED' AND expires_at>$now;
                """;
            guard.Parameters.AddWithValue("$owner", write.OwnerPrincipalId);
            guard.Parameters.AddWithValue("$session", write.SessionId);
            guard.Parameters.AddWithValue("$conversation", write.ConversationId);
            guard.Parameters.AddWithValue("$now", FormatTimestamp(DateTimeOffset.UtcNow));
            if (await guard.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null) return false;
        }

        await InsertRealtimeMessageAsync(connection, transaction, write.UserMessage, cancellationToken).ConfigureAwait(false);
        if (write.AssistantMessage is not null)
            await InsertRealtimeMessageAsync(connection, transaction, write.AssistantMessage, cancellationToken).ConfigureAwait(false);

        await using (var receipt = connection.CreateCommand())
        {
            receipt.Transaction = transaction;
            receipt.CommandText = """
                INSERT INTO realtime_turn_receipts(owner_principal_id,session_id,client_turn_id,input_item_id,
                    output_item_id,user_message_id,assistant_message_id,assistant_disposition,created_at)
                VALUES($owner,$session,$turn,$input,$output,$user,$assistant,$disposition,$created);
                """;
            receipt.Parameters.AddWithValue("$owner", write.OwnerPrincipalId);
            receipt.Parameters.AddWithValue("$session", write.SessionId);
            receipt.Parameters.AddWithValue("$turn", write.Receipt.ClientTurnId);
            receipt.Parameters.AddWithValue("$input", write.Receipt.InputItemId);
            receipt.Parameters.AddWithValue("$output", (object?)write.Receipt.OutputItemId ?? DBNull.Value);
            receipt.Parameters.AddWithValue("$user", write.Receipt.UserMessageId);
            receipt.Parameters.AddWithValue("$assistant", (object?)write.Receipt.AssistantMessageId ?? DBNull.Value);
            receipt.Parameters.AddWithValue("$disposition", write.Receipt.AssistantDisposition);
            receipt.Parameters.AddWithValue("$created", FormatTimestamp(write.Receipt.CreatedAt));
            await receipt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var item in write.Events)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO execution_events(owner_principal_id,event_id,execution_id,sequence,event_type,
                    occurred_at,message_id,capability_call_id,action_id,data_json)
                SELECT $owner,$event,$execution,COALESCE(MAX(sequence),0)+1,$type,$at,$message,$call,$action,$data
                FROM execution_events WHERE owner_principal_id=$owner AND execution_id=$execution;
                """;
            command.Parameters.AddWithValue("$owner", item.OwnerPrincipalId);
            command.Parameters.AddWithValue("$event", item.EventId);
            command.Parameters.AddWithValue("$execution", item.ExecutionId);
            command.Parameters.AddWithValue("$type", item.EventType);
            command.Parameters.AddWithValue("$at", FormatTimestamp(item.OccurredAt));
            command.Parameters.AddWithValue("$message", (object?)item.MessageId ?? DBNull.Value);
            command.Parameters.AddWithValue("$call", (object?)item.CapabilityCallId ?? DBNull.Value);
            command.Parameters.AddWithValue("$action", (object?)item.ActionId ?? DBNull.Value);
            command.Parameters.AddWithValue("$data", item.DataJson);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var idempotency = connection.CreateCommand())
        {
            idempotency.Transaction = transaction;
            idempotency.CommandText = """
                INSERT INTO idempotency_receipts(owner_principal_id,route_family,idempotency_key,request_hash,
                    response_status,response_body_json,resource_type,resource_id,created_at)
                VALUES($owner,'realtime-turn',$key,$hash,201,$body,'realtime_turn',$turn,$created);
                """;
            idempotency.Parameters.AddWithValue("$owner", write.OwnerPrincipalId);
            idempotency.Parameters.AddWithValue("$key", write.IdempotencyKey);
            idempotency.Parameters.AddWithValue("$hash", write.RequestHash);
            idempotency.Parameters.AddWithValue("$body", JsonSerializer.Serialize(new
            {
                sessionId = write.SessionId,
                clientTurnId = write.Receipt.ClientTurnId,
                userMessageId = write.Receipt.UserMessageId,
                assistantMessageId = write.Receipt.AssistantMessageId,
            }));
            idempotency.Parameters.AddWithValue("$turn", write.Receipt.ClientTurnId);
            idempotency.Parameters.AddWithValue("$created", FormatTimestamp(write.Receipt.CreatedAt));
            await idempotency.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<RealtimeEndResult?> EndRealtimeSessionAsync(
        string ownerPrincipalId, string sessionId, string reason, string idempotencyKey,
        string requestHash, DateTimeOffset endedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var prior = await ReadReceiptAsync(connection, transaction, ownerPrincipalId, "realtime-end",
            idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (prior is not null)
        {
            if (prior.RequestHash != requestHash) throw new ProductConcurrencyException("Idempotency conflict.");
            var replay = await ReadRealtimeEndAsync(connection, transaction, ownerPrincipalId, sessionId,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return replay is null ? null : replay with { Replayed = true };
        }
        var current = await ReadRealtimeEndAsync(connection, transaction, ownerPrincipalId, sessionId,
            cancellationToken).ConfigureAwait(false);
        if (current is null) return null;
        if (current.Reason.Length > 0 && current.Reason != reason)
            throw new ProductConcurrencyException("End reason conflict.");
        if (current.Reason.Length == 0)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE realtime_session_receipts
                SET state='CLIENT_ENDED',ended_at=$ended,end_reason=$reason,version=version+1
                WHERE owner_principal_id=$owner AND session_id=$session AND state='NEGOTIATED';
                """;
            update.Parameters.AddWithValue("$owner", ownerPrincipalId);
            update.Parameters.AddWithValue("$session", sessionId);
            update.Parameters.AddWithValue("$reason", reason);
            update.Parameters.AddWithValue("$ended", FormatTimestamp(endedAt));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            current = await ReadRealtimeEndAsync(connection, transaction, ownerPrincipalId, sessionId,
                cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("Realtime session disappeared.");
            await InsertRealtimeEventAsync(connection, transaction, ownerPrincipalId, sessionId,
                "realtime_ended", JsonSerializer.Serialize(new { sessionId, reason }), endedAt,
                cancellationToken).ConfigureAwait(false);
        }
        await using (var receipt = connection.CreateCommand())
        {
            receipt.Transaction = transaction;
            receipt.CommandText = """
                INSERT INTO idempotency_receipts(owner_principal_id,route_family,idempotency_key,request_hash,
                    response_status,response_body_json,resource_type,resource_id,created_at)
                VALUES($owner,'realtime-end',$key,$hash,200,$body,'realtime_session',$session,$created);
                """;
            receipt.Parameters.AddWithValue("$owner", ownerPrincipalId);
            receipt.Parameters.AddWithValue("$key", idempotencyKey);
            receipt.Parameters.AddWithValue("$hash", requestHash);
            receipt.Parameters.AddWithValue("$body", JsonSerializer.Serialize(new { id = sessionId, reason, version = current.Version }));
            receipt.Parameters.AddWithValue("$session", sessionId);
            receipt.Parameters.AddWithValue("$created", FormatTimestamp(endedAt));
            await receipt.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return current;
    }

    private async Task<bool> CompleteRealtimeNegotiationCoreAsync(string owner, string session, long generation,
        DateTimeOffset negotiatedAt, DateTimeOffset expiresAt, CancellationToken token)
    {
        await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE realtime_session_receipts
                SET state='NEGOTIATED',negotiated_at=$negotiated,expires_at=$expires,version=version+1
                WHERE owner_principal_id=$owner AND session_id=$session
                  AND negotiation_generation=$generation AND state='NEGOTIATING';
                """;
            command.Parameters.AddWithValue("$negotiated", FormatTimestamp(negotiatedAt));
            command.Parameters.AddWithValue("$expires", FormatTimestamp(expiresAt));
            command.Parameters.AddWithValue("$owner", owner);
            command.Parameters.AddWithValue("$session", session);
            command.Parameters.AddWithValue("$generation", generation);
            if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return false;
        }
        await InsertRealtimeEventAsync(connection, transaction, owner, session, "realtime_negotiated",
            JsonSerializer.Serialize(new { sessionId = session, expiresAt }), negotiatedAt, token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return true;
    }

    private static async Task InsertRealtimeEventAsync(SqliteConnection connection, SqliteTransaction transaction,
        string owner, string session, string type, string dataJson, DateTimeOffset occurredAt, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO execution_events(owner_principal_id,event_id,execution_id,sequence,event_type,
                occurred_at,message_id,capability_call_id,action_id,data_json)
            SELECT $owner,$event,$session,COALESCE(MAX(sequence),0)+1,$type,$at,NULL,NULL,NULL,$data
            FROM execution_events WHERE owner_principal_id=$owner AND execution_id=$session;
            """;
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$event", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$session", session);
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$at", FormatTimestamp(occurredAt));
        command.Parameters.AddWithValue("$data", dataJson);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task<RealtimeEndResult?> ReadRealtimeEndAsync(SqliteConnection connection,
        SqliteTransaction transaction, string owner, string session, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(end_reason,''),version FROM realtime_session_receipts WHERE owner_principal_id=$owner AND session_id=$session;";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$session", session);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false)
            ? new(session, reader.GetString(0), reader.GetInt64(1), false)
            : null;
    }

    private async Task<bool> TransitionNegotiationAsync(
        string owner, string session, long generation, string state, string? failureCode,
        DateTimeOffset? negotiatedAt, DateTimeOffset? expiresAt, CancellationToken token)
    {
        await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE realtime_session_receipts
            SET state=$state,failure_code=$failure,negotiated_at=COALESCE($negotiated,negotiated_at),
                expires_at=COALESCE($expires,expires_at),version=version+1
            WHERE owner_principal_id=$owner AND session_id=$session
              AND negotiation_generation=$generation AND state='NEGOTIATING';
            """;
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$failure", (object?)failureCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$negotiated", negotiatedAt is null ? DBNull.Value : FormatTimestamp(negotiatedAt.Value));
        command.Parameters.AddWithValue("$expires", expiresAt is null ? DBNull.Value : FormatTimestamp(expiresAt.Value));
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$session", session);
        command.Parameters.AddWithValue("$generation", generation);
        return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
    }

    private static async Task InsertRealtimeMessageAsync(
        SqliteConnection connection, SqliteTransaction transaction, ChatMessage message, CancellationToken token)
    {
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO messages(owner_principal_id,message_id,conversation_id,role,status,retry_of,
                    created_at,completed_at,version)
                VALUES($owner,$id,$conversation,$role,$status,NULL,$created,$completed,$version);
                """;
            command.Parameters.AddWithValue("$owner", message.OwnerPrincipalId);
            command.Parameters.AddWithValue("$id", message.MessageId);
            command.Parameters.AddWithValue("$conversation", message.ConversationId);
            command.Parameters.AddWithValue("$role", message.Role);
            command.Parameters.AddWithValue("$status", message.Status);
            command.Parameters.AddWithValue("$created", FormatTimestamp(message.CreatedAt));
            command.Parameters.AddWithValue("$completed", message.CompletedAt is null ? DBNull.Value : FormatTimestamp(message.CompletedAt.Value));
            command.Parameters.AddWithValue("$version", message.Version);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        foreach (var part in message.Parts)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO message_parts(owner_principal_id,part_id,message_id,sequence,kind,text,
                    capability_call_id,capability_result_id,action_id,evidence_refs_json,error_code)
                VALUES($owner,$part,$message,$sequence,$kind,$text,NULL,NULL,NULL,'[]',$error);
                """;
            command.Parameters.AddWithValue("$owner", message.OwnerPrincipalId);
            command.Parameters.AddWithValue("$part", part.PartId);
            command.Parameters.AddWithValue("$message", message.MessageId);
            command.Parameters.AddWithValue("$sequence", part.Sequence);
            command.Parameters.AddWithValue("$kind", part.Kind);
            command.Parameters.AddWithValue("$text", (object?)part.Text ?? DBNull.Value);
            command.Parameters.AddWithValue("$error", (object?)part.ErrorCode ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }

    private static async Task<RealtimeSessionReceipt?> ReadRealtimeSessionAsync(
        SqliteCommand command, CancellationToken token)
    {
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false)
            ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt64(7),
                ParseTimestamp(reader.GetString(8)), reader.GetString(9), reader.GetString(10),
                reader.GetString(11), ReadNullableTimestamp(reader, 12), ParseTimestamp(reader.GetString(13)),
                ReadNullableTimestamp(reader, 14), ReadNullableString(reader, 15), ReadNullableString(reader, 16),
                reader.GetInt64(17))
            : null;
    }

    private const string RealtimeSessionSelect = """
        SELECT owner_principal_id,session_id,conversation_id,client_attempt_id,idempotency_key_hash,
            offer_hash,state,negotiation_generation,negotiation_deadline,provider_model_id,
            provider_model_version,provider_deployment_ref,negotiated_at,expires_at,ended_at,
            end_reason,failure_code,version
        FROM realtime_session_receipts
        """;
}