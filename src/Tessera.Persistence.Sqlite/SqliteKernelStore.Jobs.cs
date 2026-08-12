using Microsoft.Data.Sqlite;
using System.Text.Json;
using Tessera.Core.Kernel;
using Tessera.Core.Product;

namespace Tessera.Persistence.Sqlite;

public sealed partial class SqliteKernelStore
{
    public async Task<IReadOnlyList<ProductJob>> ListJobsAsync(string owner,CancellationToken token=default)
    {
        await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.CommandText="SELECT job_id,name,instruction,desired_state,health,model_profile_id,schedule_json,next_occurrence,context_policy_json,created_at,updated_at,version FROM jobs WHERE owner_principal_id=$owner ORDER BY updated_at DESC,job_id;";command.Parameters.AddWithValue("$owner",owner);var values=new List<ProductJob>();
        await using(var reader=await command.ExecuteReaderAsync(token).ConfigureAwait(false)){while(await reader.ReadAsync(token).ConfigureAwait(false)){var schedule=JsonSerializer.Deserialize<JobSchedule>(reader.GetString(6))??throw new InvalidDataException("Job schedule is invalid.");values.Add(new(owner,reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),ReadNullableString(reader,5),schedule,ReadNullableTimestamp(reader,7),reader.GetString(8),[],[],[],ParseTimestamp(reader.GetString(9)),ParseTimestamp(reader.GetString(10)),reader.GetInt64(11)));}}
        for(var index=0;index<values.Count;index++)
        {var item=values[index];var accounts=await ReadJobGrantStringsAsync(connection,"job_account_grants","account_id",owner,item.JobId,token).ConfigureAwait(false);var effects=await ReadJobGrantStringsAsync(connection,"job_side_effect_grants","side_effect_class",owner,item.JobId,token).ConfigureAwait(false);var capabilities=await ReadJobCapabilitiesAsync(connection,owner,item.JobId,token).ConfigureAwait(false);values[index]=item with{AccountGrants=accounts,CapabilityGrants=capabilities,SideEffectGrants=effects};}
        return values.AsReadOnly();
    }

    public async Task<ProductJob?> GetJobAsync(string owner,string jobId,CancellationToken token=default)
        =>(await ListJobsAsync(owner,token).ConfigureAwait(false)).SingleOrDefault(item=>item.JobId==jobId);

    public async Task<ProductJob?> UpdateJobAsync(ProductJob job,long expectedVersion,CancellationToken token=default)
    {
        ProductContentValidation.Text(job.Name,nameof(job.Name),256);
        ProductContentValidation.Text(job.Instruction,nameof(job.Instruction),8192);
        using(var context=JsonDocument.Parse(job.ContextPolicyJson))ProductContentValidation.Json(context.RootElement,nameof(job.ContextPolicyJson),16384);
        await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var transaction=(SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await using(var command=connection.CreateCommand()){command.Transaction=transaction;command.CommandText="UPDATE jobs SET name=$name,instruction=$instruction,desired_state=$state,model_profile_id=$profile,schedule_json=$schedule,next_occurrence=$next,context_policy_json=$context,updated_at=$now,version=version+1 WHERE owner_principal_id=$owner AND job_id=$job AND version=$version AND desired_state<>'CANCELED';";command.Parameters.AddWithValue("$name",job.Name);command.Parameters.AddWithValue("$instruction",job.Instruction);command.Parameters.AddWithValue("$state",job.DesiredState);command.Parameters.AddWithValue("$profile",(object?)job.ModelProfileId??DBNull.Value);command.Parameters.AddWithValue("$schedule",Serialize(job.Schedule));command.Parameters.AddWithValue("$next",job.NextOccurrence is null?DBNull.Value:FormatTimestamp(job.NextOccurrence.Value));command.Parameters.AddWithValue("$context",job.ContextPolicyJson);command.Parameters.AddWithValue("$now",FormatTimestamp(DateTimeOffset.UtcNow));command.Parameters.AddWithValue("$owner",job.OwnerPrincipalId);command.Parameters.AddWithValue("$job",job.JobId);command.Parameters.AddWithValue("$version",expectedVersion);if(await command.ExecuteNonQueryAsync(token).ConfigureAwait(false)!=1)return null;}
        foreach(var table in new[]{"job_account_grants","job_capability_grants","job_side_effect_grants"}){await using var clear=connection.CreateCommand();clear.Transaction=transaction;clear.CommandText=$"DELETE FROM {table} WHERE owner_principal_id=$owner AND job_id=$job;";clear.Parameters.AddWithValue("$owner",job.OwnerPrincipalId);clear.Parameters.AddWithValue("$job",job.JobId);await clear.ExecuteNonQueryAsync(token).ConfigureAwait(false);}
        foreach(var account in job.AccountGrants)await InsertGrant(connection,transaction,"job_account_grants","account_id",job.OwnerPrincipalId,job.JobId,account,null,token).ConfigureAwait(false);
        foreach(var capability in job.CapabilityGrants)await InsertGrant(connection,transaction,"job_capability_grants","capability_id",job.OwnerPrincipalId,job.JobId,capability.Id,capability.Version,token).ConfigureAwait(false);
        foreach(var effect in job.SideEffectGrants)await InsertGrant(connection,transaction,"job_side_effect_grants","side_effect_class",job.OwnerPrincipalId,job.JobId,effect,null,token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);return await GetJobAsync(job.OwnerPrincipalId,job.JobId,token).ConfigureAwait(false);
    }

    public async Task AddJobAsync(ProductJob job,CancellationToken token=default)
    {
        ProductContentValidation.Text(job.Name,nameof(job.Name),256);
        ProductContentValidation.Text(job.Instruction,nameof(job.Instruction),8192);
        using(var context=JsonDocument.Parse(job.ContextPolicyJson))ProductContentValidation.Json(context.RootElement,nameof(job.ContextPolicyJson),16384);
        await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false); await using var transaction=(SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await using(var command=connection.CreateCommand()){command.Transaction=transaction;command.CommandText="""
            INSERT INTO jobs(owner_principal_id,job_id,name,instruction,desired_state,health,model_profile_id,schedule_json,next_occurrence,context_policy_json,created_at,updated_at,version)
            VALUES($owner,$id,$name,$instruction,$state,$health,$profile,$schedule,$next,$context,$created,$updated,$version);
            """;command.Parameters.AddWithValue("$owner",job.OwnerPrincipalId);command.Parameters.AddWithValue("$id",job.JobId);command.Parameters.AddWithValue("$name",job.Name);command.Parameters.AddWithValue("$instruction",job.Instruction);command.Parameters.AddWithValue("$state",job.DesiredState);command.Parameters.AddWithValue("$health",job.Health);command.Parameters.AddWithValue("$profile",(object?)job.ModelProfileId??DBNull.Value);command.Parameters.AddWithValue("$schedule",Serialize(job.Schedule));command.Parameters.AddWithValue("$next",job.NextOccurrence is null?DBNull.Value:FormatTimestamp(job.NextOccurrence.Value));command.Parameters.AddWithValue("$context",job.ContextPolicyJson);command.Parameters.AddWithValue("$created",FormatTimestamp(job.CreatedAt));command.Parameters.AddWithValue("$updated",FormatTimestamp(job.UpdatedAt));command.Parameters.AddWithValue("$version",job.Version);await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);}
        foreach(var account in job.AccountGrants) await InsertGrant(connection,transaction,"job_account_grants","account_id",job.OwnerPrincipalId,job.JobId,account,null,token).ConfigureAwait(false);
        foreach(var capability in job.CapabilityGrants) await InsertGrant(connection,transaction,"job_capability_grants","capability_id",job.OwnerPrincipalId,job.JobId,capability.Id,capability.Version,token).ConfigureAwait(false);
        foreach(var effect in job.SideEffectGrants) await InsertGrant(connection,transaction,"job_side_effect_grants","side_effect_class",job.OwnerPrincipalId,job.JobId,effect,null,token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    public async Task<ProductJobRun?> CreateRunOccurrenceAsync(string owner,string jobId,DateTimeOffset scheduledFor,CancellationToken token=default)
    {
        await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);var id=Guid.NewGuid().ToString("N");await using var command=connection.CreateCommand();command.CommandText="""
            INSERT INTO job_runs(owner_principal_id,run_id,job_id,scheduled_for,state,fence,version)
            VALUES($owner,$run,$job,$scheduled,'QUEUED',0,1) ON CONFLICT(owner_principal_id,job_id,scheduled_for) DO NOTHING;
            """;command.Parameters.AddWithValue("$owner",owner);command.Parameters.AddWithValue("$run",id);command.Parameters.AddWithValue("$job",jobId);command.Parameters.AddWithValue("$scheduled",FormatTimestamp(scheduledFor));if(await command.ExecuteNonQueryAsync(token).ConfigureAwait(false)!=1)return null;return new(owner,id,jobId,scheduledFor,"QUEUED",0,1);
    }

    public async Task<ProductJobRun?> CreateManualRunAsync(string owner,string jobId,string runId,long expectedJobVersion,DateTimeOffset scheduledFor,CancellationToken token=default)
    {
        var existing=await GetJobRunAsync(owner,runId,token).ConfigureAwait(false);if(existing is not null)return existing.JobId==jobId?existing:null;
        await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.CommandText="""
            INSERT INTO job_runs(owner_principal_id,run_id,job_id,scheduled_for,state,fence,version)
            SELECT $owner,$run,$job,$scheduled,'QUEUED',0,1 FROM jobs
            WHERE owner_principal_id=$owner AND job_id=$job AND version=$version AND desired_state='ACTIVE'
            ON CONFLICT(owner_principal_id,run_id) DO NOTHING;
            """;command.Parameters.AddWithValue("$owner",owner);command.Parameters.AddWithValue("$run",runId);command.Parameters.AddWithValue("$job",jobId);command.Parameters.AddWithValue("$scheduled",FormatTimestamp(scheduledFor));command.Parameters.AddWithValue("$version",expectedJobVersion);if(await command.ExecuteNonQueryAsync(token).ConfigureAwait(false)!=1)return null;return await GetJobRunAsync(owner,runId,token).ConfigureAwait(false);
    }

    public async Task<bool> SetJobDesiredStateAsync(string owner,string jobId,long expectedVersion,string desiredState,CancellationToken token=default)
    {var now=DateTimeOffset.UtcNow;await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var transaction=(SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText="UPDATE jobs SET desired_state=$state,updated_at=$now,version=version+1 WHERE owner_principal_id=$owner AND job_id=$job AND version=$expected AND desired_state<>'CANCELED';";command.Parameters.AddWithValue("$state",desiredState);command.Parameters.AddWithValue("$now",FormatTimestamp(now));command.Parameters.AddWithValue("$owner",owner);command.Parameters.AddWithValue("$job",jobId);command.Parameters.AddWithValue("$expected",expectedVersion);if(await command.ExecuteNonQueryAsync(token).ConfigureAwait(false)!=1)return false;if(desiredState=="CANCELED"){await using var runs=connection.CreateCommand();runs.Transaction=transaction;runs.CommandText="UPDATE job_runs SET state='CANCELED',ended_at=$now,error_code='job_canceled',version=version+1 WHERE owner_principal_id=$owner AND job_id=$job AND state='QUEUED';";runs.Parameters.AddWithValue("$now",FormatTimestamp(now));runs.Parameters.AddWithValue("$owner",owner);runs.Parameters.AddWithValue("$job",jobId);await runs.ExecuteNonQueryAsync(token).ConfigureAwait(false);}await transaction.CommitAsync(token).ConfigureAwait(false);return true;}

    public async Task<long?> AcquireRunLeaseAsync(string owner,string runId,string holder,DateTimeOffset now,TimeSpan duration,CancellationToken token=default)
    {
        await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.CommandText="""
            INSERT INTO scheduler_leases(owner_principal_id,run_id,holder_id,acquired_at,expires_at,fence)
            VALUES($owner,$run,$holder,$now,$expires,1)
            ON CONFLICT(owner_principal_id,run_id) DO UPDATE SET holder_id=$holder,acquired_at=$now,expires_at=$expires,fence=scheduler_leases.fence+1
            WHERE scheduler_leases.expires_at <= $now RETURNING fence;
            """;command.Parameters.AddWithValue("$owner",owner);command.Parameters.AddWithValue("$run",runId);command.Parameters.AddWithValue("$holder",holder);command.Parameters.AddWithValue("$now",FormatTimestamp(now));command.Parameters.AddWithValue("$expires",FormatTimestamp(now+duration));var value=await command.ExecuteScalarAsync(token).ConfigureAwait(false);return value is null?null:Convert.ToInt64(value,System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<bool> AddRunCheckpointAsync(string owner,string runId,long sequence,string step,string stateJson,long fence,DateTimeOffset now,CancellationToken token=default)
    {
        await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.CommandText="""
            INSERT INTO job_run_checkpoints(owner_principal_id,run_id,sequence,step,state_json,fence,created_at)
            SELECT $owner,$run,$sequence,$step,$state,$fence,$now FROM scheduler_leases
            WHERE owner_principal_id=$owner AND run_id=$run AND fence=$fence AND expires_at>$now;
            """;command.Parameters.AddWithValue("$owner",owner);command.Parameters.AddWithValue("$run",runId);command.Parameters.AddWithValue("$sequence",sequence);command.Parameters.AddWithValue("$step",step);command.Parameters.AddWithValue("$state",stateJson);command.Parameters.AddWithValue("$fence",fence);command.Parameters.AddWithValue("$now",FormatTimestamp(now));return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false)==1;
    }

        public async Task<bool> SetRunContextSnapshotAsync(string owner,string runId,string snapshotRef,long fence,DateTimeOffset now,CancellationToken token=default)
        {await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.CommandText="""
                UPDATE job_runs SET context_snapshot_ref=$snapshot,version=version+1
                WHERE owner_principal_id=$owner AND run_id=$run AND state='RUNNING' AND fence=$fence
                    AND EXISTS(SELECT 1 FROM scheduler_leases WHERE owner_principal_id=$owner AND run_id=$run AND fence=$fence AND expires_at>$now);
                """;command.Parameters.AddWithValue("$snapshot",snapshotRef);command.Parameters.AddWithValue("$owner",owner);command.Parameters.AddWithValue("$run",runId);command.Parameters.AddWithValue("$fence",fence);command.Parameters.AddWithValue("$now",FormatTimestamp(now));return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false)==1;}

    public async Task<bool> WaitForRunApprovalAsync(
        string owner,
        string runId,
        long fence,
        string actionId,
        ExecutionRequest request,
        DateTimeOffset now,
        CancellationToken token = default)
    {
        if (!string.Equals(owner, request.OwnerPrincipalId, StringComparison.Ordinal)
            || !string.Equals(runId, request.JobRunId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Job checkpoint request does not match the run owner and ID.");
        var action = await GetActionAsync(owner, actionId, token).ConfigureAwait(false);
        if (action?.State != ActionState.Proposed
            || action.R2Binding?.JobRunId != runId
            || action.R2Binding.JobId != request.JobId)
            throw new InvalidOperationException("Only the exact proposed Action can suspend this Job run.");

        await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE job_runs SET state='WAITING_FOR_APPROVAL',version=version+1
                WHERE owner_principal_id=$owner AND run_id=$run AND state='RUNNING' AND fence=$fence
                  AND EXISTS(SELECT 1 FROM scheduler_leases WHERE owner_principal_id=$owner AND run_id=$run
                      AND fence=$fence AND expires_at>$now);
                """;
            update.Parameters.AddWithValue("$owner", owner);
            update.Parameters.AddWithValue("$run", runId);
            update.Parameters.AddWithValue("$fence", fence);
            update.Parameters.AddWithValue("$now", FormatTimestamp(now));
            if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return false;
        }

        await using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.Transaction = transaction;
            checkpoint.CommandText = """
                INSERT INTO job_run_checkpoints(owner_principal_id,run_id,sequence,step,state_json,fence,created_at)
                SELECT $owner,$run,COALESCE(MAX(sequence),0)+1,'WAITING_FOR_APPROVAL',$state,$fence,$now
                FROM job_run_checkpoints WHERE owner_principal_id=$owner AND run_id=$run;
                """;
            checkpoint.Parameters.AddWithValue("$owner", owner);
            checkpoint.Parameters.AddWithValue("$run", runId);
            checkpoint.Parameters.AddWithValue("$state", JsonSerializer.Serialize(new { actionId, request }));
            checkpoint.Parameters.AddWithValue("$fence", fence);
            checkpoint.Parameters.AddWithValue("$now", FormatTimestamp(now));
            await checkpoint.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        await using (var release = connection.CreateCommand())
        {
            release.Transaction = transaction;
            release.CommandText = "UPDATE scheduler_leases SET expires_at=$now WHERE owner_principal_id=$owner AND run_id=$run AND fence=$fence;";
            release.Parameters.AddWithValue("$owner", owner);
            release.Parameters.AddWithValue("$run", runId);
            release.Parameters.AddWithValue("$fence", fence);
            release.Parameters.AddWithValue("$now", FormatTimestamp(now));
            await release.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return true;
    }

    public async Task<ProductJobRun?> ResolveWaitingRunAsync(
        string owner,
        string runId,
        long fence,
        DateTimeOffset now,
        CancellationToken token = default)
    {
        await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
        string actionState;
        string? failure;
        string actionId;
        await using (var action = connection.CreateCommand())
        {
            action.CommandText = """
                SELECT a.state,a.failure,j.state,a.action_id FROM actions a
                JOIN durable_execution_requests r ON r.owner_principal_id=a.owner_principal_id AND r.action_id=a.action_id
                JOIN job_runs j ON j.owner_principal_id=r.owner_principal_id AND j.run_id=r.job_run_id
                WHERE r.owner_principal_id=$owner AND r.job_run_id=$run
                ORDER BY a.created_at DESC,a.action_id LIMIT 1;
                """;
            action.Parameters.AddWithValue("$owner", owner);
            action.Parameters.AddWithValue("$run", runId);
            await using var reader = await action.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            actionState = reader.GetString(0);
            failure = ReadNullableString(reader, 1);
            actionId=reader.GetString(3);
            var runState = reader.GetString(2);
            if (runState == "RECONCILIATION_REQUIRED" && actionState == "RECONCILIATION_REQUIRED") return null;
        }

        var targetState = actionState switch
        {
            "EXTERNALLY_CONFIRMED" => "SUCCEEDED",
            "EXECUTION_SUCCEEDED" => "SUCCEEDED",
            "PROVIDER_VERIFIED" => "SUCCEEDED",
            "FAILED" => "FAILED",
            "RECONCILIATION_REQUIRED" => "RECONCILIATION_REQUIRED",
            "CANCELED" => "CANCELED",
            "EXPIRED" => "CANCELED",
            _ => null,
        };
        if (targetState is null) return null;

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE job_runs SET state=$state,ended_at=$ended,error_code=$error,fence=$fence,
                version=version+CASE WHEN state='WAITING_FOR_APPROVAL' THEN 2 ELSE 1 END
            WHERE owner_principal_id=$owner AND run_id=$run AND state IN ('WAITING_FOR_APPROVAL','RECONCILIATION_REQUIRED')
              AND EXISTS(SELECT 1 FROM scheduler_leases WHERE owner_principal_id=$owner AND run_id=$run
                  AND fence=$fence AND expires_at>$now);
            """;
        command.Parameters.AddWithValue("$state", targetState);
        command.Parameters.AddWithValue("$ended", targetState == "RECONCILIATION_REQUIRED" ? DBNull.Value : FormatTimestamp(now));
        command.Parameters.AddWithValue("$error", (object?)failure ?? DBNull.Value);
        command.Parameters.AddWithValue("$fence", fence);
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return null;
        if(targetState=="SUCCEEDED")
        {
            await using var output=connection.CreateCommand();output.Transaction=transaction;output.CommandText="""
                INSERT OR IGNORE INTO job_outputs(owner_principal_id,output_ref,run_id,kind,media_type,summary,text,truncated,created_at)
                VALUES($owner,$ref,$run,'ACTION','text/plain','Approved action completed',$text,0,$now);
                """;output.Parameters.AddWithValue("$owner",owner);output.Parameters.AddWithValue("$ref",$"output:{runId}:{actionId}");output.Parameters.AddWithValue("$run",runId);output.Parameters.AddWithValue("$text",$"Action {actionId} was approved, executed, and externally confirmed.");output.Parameters.AddWithValue("$now",FormatTimestamp(now));await output.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            await using var checkpoint=connection.CreateCommand();checkpoint.Transaction=transaction;checkpoint.CommandText="""
                INSERT INTO job_run_checkpoints(owner_principal_id,run_id,sequence,step,state_json,fence,created_at)
                SELECT $owner,$run,COALESCE(MAX(sequence),0)+1,'ACTION_CONFIRMED',$state,$fence,$now
                FROM job_run_checkpoints WHERE owner_principal_id=$owner AND run_id=$run;
                """;checkpoint.Parameters.AddWithValue("$owner",owner);checkpoint.Parameters.AddWithValue("$run",runId);checkpoint.Parameters.AddWithValue("$state",JsonSerializer.Serialize(new{actionId}));checkpoint.Parameters.AddWithValue("$fence",fence);checkpoint.Parameters.AddWithValue("$now",FormatTimestamp(now));await checkpoint.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return await GetJobRunAsync(owner, runId, token).ConfigureAwait(false);
    }

    public async Task<ProductJobRun?> GetJobRunAsync(string owner,string runId,CancellationToken token=default)
    {await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.CommandText="SELECT job_id,scheduled_for,state,fence,version,started_at,ended_at,model_profile_id,context_snapshot_ref,error_code FROM job_runs WHERE owner_principal_id=$owner AND run_id=$run;";command.Parameters.AddWithValue("$owner",owner);command.Parameters.AddWithValue("$run",runId);await using var reader=await command.ExecuteReaderAsync(token).ConfigureAwait(false);return await reader.ReadAsync(token).ConfigureAwait(false)?new(owner,runId,reader.GetString(0),ParseTimestamp(reader.GetString(1)),reader.GetString(2),reader.GetInt64(3),reader.GetInt64(4),ReadNullableTimestamp(reader,5),ReadNullableTimestamp(reader,6),ReadNullableString(reader,7),ReadNullableString(reader,8),ReadNullableString(reader,9)):null;}

    public async Task<IReadOnlyList<ProductJobRun>> ListJobRunsAsync(string owner,string? jobId=null,CancellationToken token=default)
    {await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.CommandText="SELECT run_id FROM job_runs WHERE owner_principal_id=$owner AND ($job IS NULL OR job_id=$job) ORDER BY scheduled_for DESC,run_id LIMIT 100;";command.Parameters.AddWithValue("$owner",owner);command.Parameters.AddWithValue("$job",(object?)jobId??DBNull.Value);var ids=new List<string>();await using(var reader=await command.ExecuteReaderAsync(token).ConfigureAwait(false)){while(await reader.ReadAsync(token).ConfigureAwait(false))ids.Add(reader.GetString(0));}var values=new List<ProductJobRun>();foreach(var id in ids){var value=await GetJobRunAsync(owner,id,token).ConfigureAwait(false);if(value is not null)values.Add(value);}return values.AsReadOnly();}

    public async Task<IReadOnlyList<ProductJobRun>> ListWaitingRunsAsync(CancellationToken token=default)
    {await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.CommandText="SELECT owner_principal_id,run_id FROM job_runs WHERE state IN ('WAITING_FOR_APPROVAL','RECONCILIATION_REQUIRED') ORDER BY scheduled_for LIMIT 100;";var ids=new List<(string Owner,string Run)>();await using(var reader=await command.ExecuteReaderAsync(token).ConfigureAwait(false)){while(await reader.ReadAsync(token).ConfigureAwait(false))ids.Add((reader.GetString(0),reader.GetString(1)));}var values=new List<ProductJobRun>();foreach(var id in ids){var value=await GetJobRunAsync(id.Owner,id.Run,token).ConfigureAwait(false);if(value is not null)values.Add(value);}return values.AsReadOnly();}

        public async Task<int> RecoverExpiredRunningRunsAsync(DateTimeOffset now,CancellationToken token=default)
        {
                await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);
                await using var transaction=(SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
                await using var waiting=connection.CreateCommand();waiting.Transaction=transaction;waiting.CommandText="""
                        UPDATE job_runs SET state='WAITING_FOR_APPROVAL',error_code='recovered_pending_approval',version=version+1
                        WHERE state='RUNNING'
                            AND NOT EXISTS(SELECT 1 FROM scheduler_leases lease WHERE lease.owner_principal_id=job_runs.owner_principal_id AND lease.run_id=job_runs.run_id AND lease.expires_at>$now)
                            AND EXISTS(SELECT 1 FROM actions action WHERE action.owner_principal_id=job_runs.owner_principal_id AND action.job_run_id=job_runs.run_id AND action.state='PROPOSED');
                        """;waiting.Parameters.AddWithValue("$now",FormatTimestamp(now));var changed=await waiting.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                await using var reconcile=connection.CreateCommand();reconcile.Transaction=transaction;reconcile.CommandText="""
                        UPDATE job_runs SET state='RECONCILIATION_REQUIRED',error_code='expired_lease_external_outcome_unknown',version=version+1
                        WHERE state='RUNNING'
                            AND NOT EXISTS(SELECT 1 FROM scheduler_leases lease WHERE lease.owner_principal_id=job_runs.owner_principal_id AND lease.run_id=job_runs.run_id AND lease.expires_at>$now)
                            AND EXISTS(SELECT 1 FROM actions action WHERE action.owner_principal_id=job_runs.owner_principal_id AND action.job_run_id=job_runs.run_id AND action.state IN ('STARTED','RECONCILIATION_REQUIRED'));
                        """;reconcile.Parameters.AddWithValue("$now",FormatTimestamp(now));changed+=await reconcile.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                await using var retry=connection.CreateCommand();retry.Transaction=transaction;retry.CommandText="""
                        UPDATE job_runs SET state=CASE WHEN EXISTS(SELECT 1 FROM jobs WHERE jobs.owner_principal_id=job_runs.owner_principal_id AND jobs.job_id=job_runs.job_id AND jobs.desired_state='CANCELED') THEN 'CANCELED' ELSE 'QUEUED' END,started_at=NULL,error_code=CASE WHEN EXISTS(SELECT 1 FROM jobs WHERE jobs.owner_principal_id=job_runs.owner_principal_id AND jobs.job_id=job_runs.job_id AND jobs.desired_state='CANCELED') THEN 'job_canceled' ELSE 'recovered_expired_read_run' END,ended_at=CASE WHEN EXISTS(SELECT 1 FROM jobs WHERE jobs.owner_principal_id=job_runs.owner_principal_id AND jobs.job_id=job_runs.job_id AND jobs.desired_state='CANCELED') THEN $now ELSE NULL END,version=version+1
                        WHERE state='RUNNING'
                            AND NOT EXISTS(SELECT 1 FROM scheduler_leases lease WHERE lease.owner_principal_id=job_runs.owner_principal_id AND lease.run_id=job_runs.run_id AND lease.expires_at>$now)
                            AND NOT EXISTS(SELECT 1 FROM actions action WHERE action.owner_principal_id=job_runs.owner_principal_id AND action.job_run_id=job_runs.run_id);
                        """;retry.Parameters.AddWithValue("$now",FormatTimestamp(now));changed+=await retry.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);return changed;
        }

    public async Task<int> ScheduleDueRunsAsync(DateTimeOffset now,CancellationToken token=default)
    {
        await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var transaction=(SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);await using var select=connection.CreateCommand();select.Transaction=transaction;select.CommandText="SELECT owner_principal_id,job_id,schedule_json,next_occurrence,version FROM jobs WHERE desired_state='ACTIVE' AND next_occurrence IS NOT NULL AND next_occurrence<=$now ORDER BY next_occurrence LIMIT 100;";select.Parameters.AddWithValue("$now",FormatTimestamp(now));var due=new List<(string Owner,string Job,string Schedule,DateTimeOffset Occurrence,long Version)>();await using(var reader=await select.ExecuteReaderAsync(token).ConfigureAwait(false)){while(await reader.ReadAsync(token).ConfigureAwait(false))due.Add((reader.GetString(0),reader.GetString(1),reader.GetString(2),ParseTimestamp(reader.GetString(3)),reader.GetInt64(4)));}
        var created=0;foreach(var item in due){var schedule=System.Text.Json.JsonSerializer.Deserialize<JobSchedule>(item.Schedule)??throw new InvalidDataException("Job schedule is invalid.");var next=JobScheduleCalculator.Next(schedule,item.Occurrence);await using var run=connection.CreateCommand();run.Transaction=transaction;run.CommandText="INSERT INTO job_runs(owner_principal_id,run_id,job_id,scheduled_for,state,fence,version) VALUES($owner,$run,$job,$scheduled,'QUEUED',0,1) ON CONFLICT(owner_principal_id,job_id,scheduled_for) DO NOTHING;";run.Parameters.AddWithValue("$owner",item.Owner);run.Parameters.AddWithValue("$run",Guid.NewGuid().ToString("N"));run.Parameters.AddWithValue("$job",item.Job);run.Parameters.AddWithValue("$scheduled",FormatTimestamp(item.Occurrence));created+=await run.ExecuteNonQueryAsync(token).ConfigureAwait(false);await using var advance=connection.CreateCommand();advance.Transaction=transaction;advance.CommandText="UPDATE jobs SET next_occurrence=$next,updated_at=$now,version=version+1 WHERE owner_principal_id=$owner AND job_id=$job AND version=$version AND next_occurrence=$scheduled;";advance.Parameters.AddWithValue("$next",next is null?DBNull.Value:FormatTimestamp(next.Value));advance.Parameters.AddWithValue("$now",FormatTimestamp(now));advance.Parameters.AddWithValue("$owner",item.Owner);advance.Parameters.AddWithValue("$job",item.Job);advance.Parameters.AddWithValue("$version",item.Version);advance.Parameters.AddWithValue("$scheduled",FormatTimestamp(item.Occurrence));if(await advance.ExecuteNonQueryAsync(token).ConfigureAwait(false)!=1)throw new ProductConcurrencyException("Job changed during scheduling.");}
        await transaction.CommitAsync(token).ConfigureAwait(false);return created;
    }

        public async Task<IReadOnlyList<ProductJobRun>> ListQueuedRunsAsync(CancellationToken token=default)
        {await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.CommandText="SELECT owner_principal_id,run_id FROM job_runs WHERE state='QUEUED' ORDER BY scheduled_for LIMIT 100;";var ids=new List<(string Owner,string Run)>();await using(var reader=await command.ExecuteReaderAsync(token).ConfigureAwait(false)){while(await reader.ReadAsync(token).ConfigureAwait(false))ids.Add((reader.GetString(0),reader.GetString(1)));}var values=new List<ProductJobRun>();foreach(var id in ids){var value=await GetJobRunAsync(id.Owner,id.Run,token).ConfigureAwait(false);if(value is not null)values.Add(value);}return values.AsReadOnly();}

        public async Task<bool> StartRunAsync(string owner,string runId,long expectedVersion,long fence,DateTimeOffset now,CancellationToken token=default)
        {await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.CommandText="""
                UPDATE job_runs SET state='RUNNING',started_at=$now,fence=$fence,version=version+1
                WHERE owner_principal_id=$owner AND run_id=$run AND state='QUEUED' AND version=$version
                    AND EXISTS(SELECT 1 FROM jobs WHERE jobs.owner_principal_id=job_runs.owner_principal_id
                        AND jobs.job_id=job_runs.job_id AND jobs.desired_state='ACTIVE')
                    AND EXISTS(SELECT 1 FROM scheduler_leases WHERE owner_principal_id=$owner AND run_id=$run AND fence=$fence AND expires_at>$now);
                """;command.Parameters.AddWithValue("$now",FormatTimestamp(now));command.Parameters.AddWithValue("$fence",fence);command.Parameters.AddWithValue("$owner",owner);command.Parameters.AddWithValue("$run",runId);command.Parameters.AddWithValue("$version",expectedVersion);return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false)==1;}

        public async Task<bool> CompleteRunAsync(string owner,string runId,long fence,string state,string? error,DateTimeOffset now,CancellationToken token=default)
        {await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.CommandText="""
                UPDATE job_runs SET state=CASE WHEN EXISTS(SELECT 1 FROM jobs WHERE jobs.owner_principal_id=job_runs.owner_principal_id AND jobs.job_id=job_runs.job_id AND jobs.desired_state='CANCELED') THEN 'CANCELED' ELSE $state END,ended_at=$now,error_code=CASE WHEN EXISTS(SELECT 1 FROM jobs WHERE jobs.owner_principal_id=job_runs.owner_principal_id AND jobs.job_id=job_runs.job_id AND jobs.desired_state='CANCELED') THEN 'job_canceled' ELSE $error END,version=version+1
                WHERE owner_principal_id=$owner AND run_id=$run AND state='RUNNING' AND fence=$fence
                    AND EXISTS(SELECT 1 FROM scheduler_leases WHERE owner_principal_id=$owner AND run_id=$run AND fence=$fence AND expires_at>$now);
                """;command.Parameters.AddWithValue("$state",state);command.Parameters.AddWithValue("$now",FormatTimestamp(now));command.Parameters.AddWithValue("$error",(object?)error??DBNull.Value);command.Parameters.AddWithValue("$owner",owner);command.Parameters.AddWithValue("$run",runId);command.Parameters.AddWithValue("$fence",fence);return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false)==1;}

        public async Task<bool> IsJobExecutionGrantedAsync(string owner,string jobId,string accountId,string capabilityId,string capabilityVersion,CancellationToken token=default)
        {await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.CommandText="""
                SELECT 1 FROM jobs j
                JOIN job_account_grants a ON a.owner_principal_id=j.owner_principal_id AND a.job_id=j.job_id
                JOIN job_capability_grants c ON c.owner_principal_id=j.owner_principal_id AND c.job_id=j.job_id
                WHERE j.owner_principal_id=$owner AND j.job_id=$job AND j.desired_state='ACTIVE'
                    AND a.account_id=$account AND c.capability_id=$capability AND c.capability_version=$capabilityVersion;
                """;command.Parameters.AddWithValue("$owner",owner);command.Parameters.AddWithValue("$job",jobId);command.Parameters.AddWithValue("$account",accountId);command.Parameters.AddWithValue("$capability",capabilityId);command.Parameters.AddWithValue("$capabilityVersion",capabilityVersion);return await command.ExecuteScalarAsync(token).ConfigureAwait(false)is not null;}

    private static async Task InsertGrant(SqliteConnection connection,SqliteTransaction transaction,string table,string column,string owner,string job,string value,string? version,CancellationToken token)
    {await using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText=version is null?$"INSERT INTO {table}(owner_principal_id,job_id,{column}) VALUES($owner,$job,$value);":$"INSERT INTO {table}(owner_principal_id,job_id,{column},capability_version) VALUES($owner,$job,$value,$version);";command.Parameters.AddWithValue("$owner",owner);command.Parameters.AddWithValue("$job",job);command.Parameters.AddWithValue("$value",value);if(version is not null)command.Parameters.AddWithValue("$version",version);await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);}

    private static async Task<IReadOnlyList<string>> ReadJobGrantStringsAsync(SqliteConnection connection,string table,string column,string owner,string job,CancellationToken token)
    {await using var command=connection.CreateCommand();command.CommandText=$"SELECT {column} FROM {table} WHERE owner_principal_id=$owner AND job_id=$job ORDER BY {column};";command.Parameters.AddWithValue("$owner",owner);command.Parameters.AddWithValue("$job",job);var values=new List<string>();await using var reader=await command.ExecuteReaderAsync(token).ConfigureAwait(false);while(await reader.ReadAsync(token).ConfigureAwait(false))values.Add(reader.GetString(0));return values.AsReadOnly();}

    private static async Task<IReadOnlyList<(string Id,string Version)>> ReadJobCapabilitiesAsync(SqliteConnection connection,string owner,string job,CancellationToken token)
    {await using var command=connection.CreateCommand();command.CommandText="SELECT capability_id,capability_version FROM job_capability_grants WHERE owner_principal_id=$owner AND job_id=$job ORDER BY capability_id,capability_version;";command.Parameters.AddWithValue("$owner",owner);command.Parameters.AddWithValue("$job",job);var values=new List<(string,string)>();await using var reader=await command.ExecuteReaderAsync(token).ConfigureAwait(false);while(await reader.ReadAsync(token).ConfigureAwait(false))values.Add((reader.GetString(0),reader.GetString(1)));return values.AsReadOnly();}

    public async Task SetJobsHealthForAccountAsync(string owner,string accountId,string health,CancellationToken token=default)
    {await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.CommandText="UPDATE jobs SET health=$health,updated_at=$now,version=version+1 WHERE owner_principal_id=$owner AND desired_state<>'CANCELED' AND EXISTS(SELECT 1 FROM job_account_grants grant_row WHERE grant_row.owner_principal_id=jobs.owner_principal_id AND grant_row.job_id=jobs.job_id AND grant_row.account_id=$account);";command.Parameters.AddWithValue("$health",health);command.Parameters.AddWithValue("$now",FormatTimestamp(DateTimeOffset.UtcNow));command.Parameters.AddWithValue("$owner",owner);command.Parameters.AddWithValue("$account",accountId);await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);}

    public Task RecomputeJobsHealthAsync(string owner,CancellationToken token=default)
        =>RecomputeJobsHealthAsync(owner,null,token);

    public async Task RecomputeJobsHealthAsync(
        string owner,
        IReadOnlySet<(string Id, string Version)>? executablePlugins,
        CancellationToken token)
    {
        var plugins=await ListPluginInstallationsAsync(owner,token).ConfigureAwait(false);var allAccounts=await ListConnectedAccountsAsync(owner,token).ConfigureAwait(false);
        foreach(var job in await ListJobsAsync(owner,token).ConfigureAwait(false))
        {
            if(job.DesiredState=="CANCELED")continue;var grantedAccounts=allAccounts.Where(account=>job.AccountGrants.Contains(account.AccountId,StringComparer.Ordinal)).ToArray();
            var accountsReady=job.AccountGrants.All(id=>grantedAccounts.Any(account=>account.AccountId==id&&account.Lifecycle==AccountLifecycle.Connected));
            var capabilitiesReady=job.CapabilityGrants.All(capability=>plugins.Any(plugin=>
            {
                if(!plugin.Enabled)return false;
                if(executablePlugins is not null
                    && plugin.PluginId is not("local" or "model-provider")
                    && !executablePlugins.Contains((plugin.PluginId,plugin.PluginVersion)))return false;
                var contract=CapabilityContract(plugin.ManifestJson,capability.Id,capability.Version);if(contract is null)return false;if(!contract.Value.AccountRequired)return true;
                return grantedAccounts.Any(account=>account.Lifecycle==AccountLifecycle.Connected&&account.PluginId==plugin.PluginId&&account.PluginVersion==plugin.PluginVersion&&contract.Value.RequiredPermissions.All(permission=>account.Permissions.Contains(permission,StringComparer.Ordinal))&&account.CapabilityBindings.Any(binding=>binding.PluginId==plugin.PluginId&&binding.PluginVersion==plugin.PluginVersion&&binding.CapabilityId==capability.Id&&binding.CapabilityVersion==capability.Version));
            }));
            var health=accountsReady&&capabilitiesReady?"READY":"BLOCKED";if(job.Health==health)continue;
            await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.CommandText="UPDATE jobs SET health=$health,updated_at=$now,version=version+1 WHERE owner_principal_id=$owner AND job_id=$job AND version=$version;";command.Parameters.AddWithValue("$health",health);command.Parameters.AddWithValue("$now",FormatTimestamp(DateTimeOffset.UtcNow));command.Parameters.AddWithValue("$owner",owner);command.Parameters.AddWithValue("$job",job.JobId);command.Parameters.AddWithValue("$version",job.Version);await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }
}