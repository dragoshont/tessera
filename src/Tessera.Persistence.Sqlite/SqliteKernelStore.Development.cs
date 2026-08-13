using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tessera.Core.Product;

namespace Tessera.Persistence.Sqlite;

public sealed partial class SqliteKernelStore
{
    public async Task RegisterDevelopmentWorkspaceAsync(
        DevelopmentWorkspace workspace,
        CancellationToken token = default)
    {
        await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO development_workspaces(
                owner_principal_id,workspace_id,conversation_id,display_name,snapshot_ref,
                snapshot_hash,state,created_at,version)
            VALUES($owner,$workspace,$conversation,$display,$snapshotRef,$snapshotHash,$state,$created,$version)
            ON CONFLICT(owner_principal_id,workspace_id) DO UPDATE SET
                display_name=excluded.display_name,state=excluded.state,version=development_workspaces.version+1
            WHERE development_workspaces.conversation_id=excluded.conversation_id
              AND development_workspaces.snapshot_ref=excluded.snapshot_ref
              AND development_workspaces.snapshot_hash=excluded.snapshot_hash
              AND (development_workspaces.display_name<>excluded.display_name OR development_workspaces.state<>excluded.state);
            """;
        command.Parameters.AddWithValue("$owner", workspace.OwnerPrincipalId);
        command.Parameters.AddWithValue("$workspace", workspace.WorkspaceId);
        command.Parameters.AddWithValue("$conversation", workspace.ConversationId);
        command.Parameters.AddWithValue("$display", workspace.DisplayName);
        command.Parameters.AddWithValue("$snapshotRef", workspace.SnapshotRef);
        command.Parameters.AddWithValue("$snapshotHash", workspace.SnapshotHash);
        command.Parameters.AddWithValue("$state", workspace.State);
        command.Parameters.AddWithValue("$created", FormatTimestamp(workspace.CreatedAt));
        command.Parameters.AddWithValue("$version", workspace.Version);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);

        var registered = await GetDevelopmentWorkspaceAsync(
            workspace.OwnerPrincipalId, workspace.ConversationId, workspace.WorkspaceId, token).ConfigureAwait(false);
        if (registered is null
            || registered.SnapshotRef != workspace.SnapshotRef
            || registered.SnapshotHash != workspace.SnapshotHash)
            throw new InvalidOperationException("Development workspace snapshots are immutable.");
    }

    public async Task<IReadOnlyList<DevelopmentWorkspace>> ListDevelopmentWorkspacesAsync(
        string owner,
        string conversationId,
        CancellationToken token = default)
    {
        await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT workspace_id,display_name,snapshot_ref,snapshot_hash,state,created_at,version
            FROM development_workspaces
            WHERE owner_principal_id=$owner AND conversation_id=$conversation AND state='READY'
            ORDER BY created_at,workspace_id;
            """;
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$conversation", conversationId);
        var values = new List<DevelopmentWorkspace>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            values.Add(new(owner, reader.GetString(0), conversationId, reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), ParseTimestamp(reader.GetString(5)), reader.GetInt64(6)));
        return values.AsReadOnly();
    }

    public async Task<DevelopmentWorkspace?> GetDevelopmentWorkspaceAsync(
        string owner,
        string conversationId,
        string workspaceId,
        CancellationToken token = default)
    {
        await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
        return await ReadDevelopmentWorkspaceAsync(connection, null, owner, conversationId, workspaceId, token)
            .ConfigureAwait(false);
    }

    public async Task<DevelopmentJobSpec?> GetDevelopmentJobSpecAsync(
        string owner,
        string jobId,
        CancellationToken token = default)
    {
        await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT workspace_id,command_profile,arguments_json,effect,timeout_seconds,
                   output_limit_bytes,executor_image_digest
            FROM development_job_specs WHERE owner_principal_id=$owner AND job_id=$job;
            """;
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$job", jobId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false)
            ? new(reader.GetString(0), reader.GetString(1),
                JsonSerializer.Deserialize<string[]>(reader.GetString(2)) ?? [], reader.GetString(3),
                reader.GetInt32(4), reader.GetInt32(5), reader.GetString(6))
            : null;
    }

    public async Task<DevelopmentTaskCreateResult> CreateDevelopmentTaskAsync(
        string owner,
        string conversationId,
        string idempotencyKey,
        string requestHash,
        string jobId,
        string runId,
        string name,
        string workspaceId,
        DevelopmentCommandProfile profile,
        string executorImageDigest,
        DateTimeOffset now,
        CancellationToken token = default)
    {
        const string routeFamily = "development-task";
        await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);

        await using (var receipt = connection.CreateCommand())
        {
            receipt.Transaction = transaction;
            receipt.CommandText = """
                SELECT request_hash,response_body_json FROM idempotency_receipts
                WHERE owner_principal_id=$owner AND route_family=$route AND idempotency_key=$key;
                """;
            receipt.Parameters.AddWithValue("$owner", owner);
            receipt.Parameters.AddWithValue("$route", routeFamily);
            receipt.Parameters.AddWithValue("$key", idempotencyKey);
            await using var reader = await receipt.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                if (!string.Equals(reader.GetString(0), requestHash, StringComparison.Ordinal))
                    return DevelopmentTaskCreateResult.Failed("idempotency_conflict");
                return DevelopmentTaskCreateResult.Replay(reader.GetString(1));
            }
        }

        await using (var conversation = connection.CreateCommand())
        {
            conversation.Transaction = transaction;
            conversation.CommandText = """
                SELECT 1 FROM conversations
                WHERE owner_principal_id=$owner AND conversation_id=$conversation AND state<>'DELETED';
                """;
            conversation.Parameters.AddWithValue("$owner", owner);
            conversation.Parameters.AddWithValue("$conversation", conversationId);
            if (await conversation.ExecuteScalarAsync(token).ConfigureAwait(false) is null)
                return DevelopmentTaskCreateResult.Failed("not_found");
        }

        var workspace = await ReadDevelopmentWorkspaceAsync(
            connection, transaction, owner, conversationId, workspaceId, token).ConfigureAwait(false);
        if (workspace is null) return DevelopmentTaskCreateResult.Failed("not_found");
        if (workspace.State != "READY") return DevelopmentTaskCreateResult.Failed("workspace_unavailable");

        var schedule = new JobSchedule("once", now, null, "UTC", null);
        var spec = new DevelopmentJobSpec(workspaceId, profile.Id, [], profile.Effect,
            profile.TimeoutSeconds, profile.OutputLimitBytes, executorImageDigest);
        var job = new ProductJob(owner, jobId, name, $"Development command profile: {profile.Id}",
            "ACTIVE", "READY", null, schedule, null, "{}", [], [], [], now, now, 1,
            "DEVELOPMENT", conversationId, spec);
        var run = new ProductJobRun(owner, runId, jobId, now, "QUEUED", 0, 1);

        await using (var insertJob = connection.CreateCommand())
        {
            insertJob.Transaction = transaction;
            insertJob.CommandText = """
                INSERT INTO jobs(owner_principal_id,job_id,name,instruction,desired_state,health,
                    model_profile_id,schedule_json,next_occurrence,context_policy_json,created_at,
                    updated_at,version,kind,conversation_id)
                VALUES($owner,$job,$name,$instruction,'ACTIVE','READY',NULL,$schedule,NULL,'{}',
                    $now,$now,1,'DEVELOPMENT',$conversation);
                """;
            insertJob.Parameters.AddWithValue("$owner", owner);
            insertJob.Parameters.AddWithValue("$job", jobId);
            insertJob.Parameters.AddWithValue("$name", name);
            insertJob.Parameters.AddWithValue("$instruction", job.Instruction);
            insertJob.Parameters.AddWithValue("$schedule", JsonSerializer.Serialize(schedule));
            insertJob.Parameters.AddWithValue("$now", FormatTimestamp(now));
            insertJob.Parameters.AddWithValue("$conversation", conversationId);
            await insertJob.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        await using (var insertSpec = connection.CreateCommand())
        {
            insertSpec.Transaction = transaction;
            insertSpec.CommandText = """
                INSERT INTO development_job_specs(owner_principal_id,job_id,conversation_id,workspace_id,
                    command_profile,arguments_json,effect,timeout_seconds,output_limit_bytes,executor_image_digest)
                VALUES($owner,$job,$conversation,$workspace,$profile,'[]',$effect,$timeout,$limit,$image);
                """;
            insertSpec.Parameters.AddWithValue("$owner", owner);
            insertSpec.Parameters.AddWithValue("$job", jobId);
            insertSpec.Parameters.AddWithValue("$conversation", conversationId);
            insertSpec.Parameters.AddWithValue("$workspace", workspaceId);
            insertSpec.Parameters.AddWithValue("$profile", profile.Id);
            insertSpec.Parameters.AddWithValue("$effect", profile.Effect);
            insertSpec.Parameters.AddWithValue("$timeout", profile.TimeoutSeconds);
            insertSpec.Parameters.AddWithValue("$limit", profile.OutputLimitBytes);
            insertSpec.Parameters.AddWithValue("$image", executorImageDigest);
            await insertSpec.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        await using (var insertRun = connection.CreateCommand())
        {
            insertRun.Transaction = transaction;
            insertRun.CommandText = """
                INSERT INTO job_runs(owner_principal_id,run_id,job_id,scheduled_for,state,fence,version)
                VALUES($owner,$run,$job,$now,'QUEUED',0,1);
                """;
            insertRun.Parameters.AddWithValue("$owner", owner);
            insertRun.Parameters.AddWithValue("$run", runId);
            insertRun.Parameters.AddWithValue("$job", jobId);
            insertRun.Parameters.AddWithValue("$now", FormatTimestamp(now));
            await insertRun.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        var responseBodyJson = DevelopmentTaskResponse.Serialize(job, run);
        await using (var insertReceipt = connection.CreateCommand())
        {
            insertReceipt.Transaction = transaction;
            insertReceipt.CommandText = """
                INSERT INTO idempotency_receipts(owner_principal_id,route_family,idempotency_key,
                    request_hash,response_status,response_body_json,resource_type,resource_id,created_at)
                VALUES($owner,$route,$key,$hash,202,$body,'development_job',$job,$now);
                """;
            insertReceipt.Parameters.AddWithValue("$owner", owner);
            insertReceipt.Parameters.AddWithValue("$route", routeFamily);
            insertReceipt.Parameters.AddWithValue("$key", idempotencyKey);
            insertReceipt.Parameters.AddWithValue("$hash", requestHash);
            insertReceipt.Parameters.AddWithValue("$body", responseBodyJson);
            insertReceipt.Parameters.AddWithValue("$job", jobId);
            insertReceipt.Parameters.AddWithValue("$now", FormatTimestamp(now));
            await insertReceipt.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return DevelopmentTaskCreateResult.Created(job, run, responseBodyJson);
    }

    public async Task<bool> CompleteDevelopmentRunAsync(
        string owner,
        string conversationId,
        string jobId,
        string runId,
        long fence,
        string state,
        string? errorCode,
        NormalizedDevelopmentOutput log,
        DateTimeOffset now,
        CancellationToken token = default)
    {
        if (state is not ("SUCCEEDED" or "FAILED" or "RECONCILIATION_REQUIRED"))
            throw new ArgumentException("Development run state is invalid.", nameof(state));
        await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var finalState = state;
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE job_runs SET
                    state=CASE WHEN EXISTS(
                        SELECT 1 FROM jobs WHERE jobs.owner_principal_id=job_runs.owner_principal_id
                          AND jobs.job_id=job_runs.job_id AND jobs.desired_state='CANCELED')
                        THEN 'CANCELED' ELSE $state END,
                    ended_at=CASE WHEN $state='RECONCILIATION_REQUIRED' THEN NULL ELSE $now END,
                    error_code=CASE WHEN EXISTS(
                        SELECT 1 FROM jobs WHERE jobs.owner_principal_id=job_runs.owner_principal_id
                          AND jobs.job_id=job_runs.job_id AND jobs.desired_state='CANCELED')
                        THEN 'job_canceled' ELSE $error END,
                    version=version+1
                WHERE owner_principal_id=$owner AND run_id=$run AND job_id=$job
                  AND state='RUNNING' AND fence=$fence
                  AND EXISTS(SELECT 1 FROM scheduler_leases
                      WHERE owner_principal_id=$owner AND run_id=$run AND fence=$fence AND expires_at>$now)
                RETURNING state;
                """;
            update.Parameters.AddWithValue("$state", state);
            update.Parameters.AddWithValue("$now", FormatTimestamp(now));
            update.Parameters.AddWithValue("$error", (object?)errorCode ?? DBNull.Value);
            update.Parameters.AddWithValue("$owner", owner);
            update.Parameters.AddWithValue("$run", runId);
            update.Parameters.AddWithValue("$job", jobId);
            update.Parameters.AddWithValue("$fence", fence);
            var updated = await update.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (updated is null) return false;
            finalState = (string)updated;
        }

        var outputValues = new[]
        {
            (Ref: $"output:{runId}:log", Kind: "DEVELOPMENT_LOG", Summary: "Development command log", Value: log),
        };
        for (var outputIndex = 0; outputIndex < outputValues.Length; outputIndex++)
        {
            var outputValue = outputValues[outputIndex];
            await using var output = connection.CreateCommand();
            output.Transaction = transaction;
            output.CommandText = """
                INSERT INTO job_outputs(owner_principal_id,output_ref,run_id,kind,media_type,summary,text,truncated,created_at)
                VALUES($owner,$ref,$run,$kind,'text/plain; charset=utf-8',$summary,$text,$truncated,$now);
                """;
            output.Parameters.AddWithValue("$owner", owner);
            output.Parameters.AddWithValue("$ref", outputValue.Ref);
            output.Parameters.AddWithValue("$run", runId);
            output.Parameters.AddWithValue("$kind", outputValue.Kind);
            output.Parameters.AddWithValue("$summary", outputValue.Summary);
            output.Parameters.AddWithValue("$text", outputValue.Value.Text);
            output.Parameters.AddWithValue("$truncated", outputValue.Value.Truncated);
            output.Parameters.AddWithValue("$now", FormatTimestamp(now.AddTicks(outputIndex)));
            await output.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        await using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.Transaction = transaction;
            checkpoint.CommandText = """
                INSERT INTO job_run_checkpoints(owner_principal_id,run_id,sequence,step,state_json,fence,created_at)
                SELECT $owner,$run,COALESCE(MAX(sequence),0)+1,'DEVELOPMENT_COMPLETED',$state,$fence,$now
                FROM job_run_checkpoints WHERE owner_principal_id=$owner AND run_id=$run;
                """;
            checkpoint.Parameters.AddWithValue("$owner", owner);
            checkpoint.Parameters.AddWithValue("$run", runId);
            checkpoint.Parameters.AddWithValue("$state", JsonSerializer.Serialize(new
            {
                state = finalState,
                errorCode,
                outputRefs = outputValues.Select(item => item.Ref).ToArray(),
            }));
            checkpoint.Parameters.AddWithValue("$fence", fence);
            checkpoint.Parameters.AddWithValue("$now", FormatTimestamp(now));
            await checkpoint.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        var messageId = $"development:{runId}";
        await using (var message = connection.CreateCommand())
        {
            message.Transaction = transaction;
            message.CommandText = """
                INSERT INTO messages(owner_principal_id,message_id,conversation_id,role,status,retry_of,created_at,completed_at,version)
                VALUES($owner,$message,$conversation,'SYSTEM_EVENT',$status,NULL,$now,$now,1);
                """;
            message.Parameters.AddWithValue("$owner", owner);
            message.Parameters.AddWithValue("$message", messageId);
            message.Parameters.AddWithValue("$conversation", conversationId);
            message.Parameters.AddWithValue("$status", finalState == "SUCCEEDED" ? "COMPLETED" : "FAILED");
            message.Parameters.AddWithValue("$now", FormatTimestamp(now));
            await message.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        await using (var part = connection.CreateCommand())
        {
            part.Transaction = transaction;
            part.CommandText = """
                INSERT INTO message_parts(owner_principal_id,part_id,message_id,sequence,kind,text,
                    capability_call_id,capability_result_id,action_id,evidence_refs_json,error_code)
                VALUES($owner,$part,$message,1,'STATUS',$text,NULL,NULL,NULL,'[]',$error);
                """;
            part.Parameters.AddWithValue("$owner", owner);
            part.Parameters.AddWithValue("$part", $"development:{runId}:status");
            part.Parameters.AddWithValue("$message", messageId);
            part.Parameters.AddWithValue("$text",
                $"Development job {jobId} run {runId} entered {finalState}. Output: output:{runId}:log.");
            part.Parameters.AddWithValue("$error", (object?)errorCode ?? DBNull.Value);
            await part.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return true;
    }

    private static async Task<DevelopmentWorkspace?> ReadDevelopmentWorkspaceAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string owner,
        string conversationId,
        string workspaceId,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT display_name,snapshot_ref,snapshot_hash,state,created_at,version
            FROM development_workspaces
            WHERE owner_principal_id=$owner AND conversation_id=$conversation AND workspace_id=$workspace;
            """;
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$workspace", workspaceId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false)
            ? new(owner, workspaceId, conversationId, reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), ParseTimestamp(reader.GetString(4)), reader.GetInt64(5))
            : null;
    }
}