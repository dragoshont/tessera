using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tessera.Core.Kernel;
using Tessera.Core.Product;

namespace Tessera.Persistence.Sqlite;

public sealed partial class SqliteKernelStore : ICapabilityTraceRepository
{
    public async Task BeginCapabilityCallAsync(ExecutionRequest request,DateTimeOffset now,CancellationToken token=default)
    {
        var persistedInput=request.CapabilityId=="model.chat.complete"
            ?JsonSerializer.Serialize(new{promptPersisted=false,toolCount=request.Input.TryGetProperty("tools",out var tools)&&tools.ValueKind==JsonValueKind.Array?tools.GetArrayLength():0,continuation=request.Input.TryGetProperty("assistantMessage",out _)})
            :request.Input.GetRawText();
        await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.CommandText="""
            INSERT OR IGNORE INTO capability_calls(
                owner_principal_id,call_id,execution_id,conversation_id,message_id,job_id,job_run_id,
                plugin_id,plugin_version,capability_id,capability_version,account_id,input_json,input_hash,
                state,created_at,completed_at,error_code,version)
            VALUES($owner,$call,$execution,$conversation,$message,$job,$run,$plugin,$pluginVersion,$capability,
                $capabilityVersion,$account,$input,$hash,'REQUESTED',$created,NULL,NULL,1);
            """;
        command.Parameters.AddWithValue("$owner",request.OwnerPrincipalId);command.Parameters.AddWithValue("$call",request.ExecutionId);command.Parameters.AddWithValue("$execution",request.ExecutionId);command.Parameters.AddWithValue("$conversation",(object?)request.ConversationId??DBNull.Value);command.Parameters.AddWithValue("$message",(object?)request.MessageId??DBNull.Value);command.Parameters.AddWithValue("$job",(object?)request.JobId??DBNull.Value);command.Parameters.AddWithValue("$run",(object?)request.JobRunId??DBNull.Value);command.Parameters.AddWithValue("$plugin",request.PluginId);command.Parameters.AddWithValue("$pluginVersion",request.PluginVersion);command.Parameters.AddWithValue("$capability",request.CapabilityId);command.Parameters.AddWithValue("$capabilityVersion",request.CapabilityVersion);command.Parameters.AddWithValue("$account",(object?)request.AccountId??DBNull.Value);command.Parameters.AddWithValue("$input",persistedInput);command.Parameters.AddWithValue("$hash",CapabilityPayloadHash.Compute(request.Input));command.Parameters.AddWithValue("$created",FormatTimestamp(now));await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    public async Task<CapabilityResult?> GetCompletedCapabilityResultAsync(ExecutionRequest request,CancellationToken token=default)
    {
        await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.CommandText="""
            SELECT result.data_json FROM capability_calls call
            JOIN capability_results result ON result.owner_principal_id=call.owner_principal_id AND result.call_id=call.call_id
            WHERE call.owner_principal_id=$owner AND call.call_id=$call AND call.state='SUCCEEDED'
              AND call.plugin_id=$plugin AND call.plugin_version=$pluginVersion
              AND call.capability_id=$capability AND call.capability_version=$capabilityVersion
              AND call.account_id IS $account AND call.input_hash=$hash;
            """;command.Parameters.AddWithValue("$owner",request.OwnerPrincipalId);command.Parameters.AddWithValue("$call",request.ExecutionId);command.Parameters.AddWithValue("$plugin",request.PluginId);command.Parameters.AddWithValue("$pluginVersion",request.PluginVersion);command.Parameters.AddWithValue("$capability",request.CapabilityId);command.Parameters.AddWithValue("$capabilityVersion",request.CapabilityVersion);command.Parameters.AddWithValue("$account",(object?)request.AccountId??DBNull.Value);command.Parameters.AddWithValue("$hash",CapabilityPayloadHash.Compute(request.Input));var data=await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;if(data is null)return null;using var document=JsonDocument.Parse(data);return new(CapabilityOutcome.Succeeded,document.RootElement.Clone(),null,null,null);
    }

    public async Task<int> ResetInterruptedCapabilityCallsAsync(string owner,string rootExecutionId,CancellationToken token=default)
    {await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.CommandText="UPDATE capability_calls SET state='REQUESTED',completed_at=NULL,error_code=NULL,version=version+1 WHERE owner_principal_id=$owner AND state='RUNNING' AND (execution_id=$execution OR substr(execution_id,1,length($execution)+1)=$execution||':');";command.Parameters.AddWithValue("$owner",owner);command.Parameters.AddWithValue("$execution",rootExecutionId);return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);}

        public async Task<bool> TryStartCapabilityCallAsync(ExecutionRequest request,DateTimeOffset now,CancellationToken token=default)
        {
            var installation=await GetPluginInstallationAsync(request.OwnerPrincipalId,request.PluginId,request.PluginVersion,token).ConfigureAwait(false);
            var requiredPermissions=installation is null?[]:CapabilityContract(installation.ManifestJson,request.CapabilityId,request.CapabilityVersion)?.RequiredPermissions??[];
                await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.CommandText="""
                        UPDATE capability_calls SET state='RUNNING',version=version+1
                        WHERE owner_principal_id=$owner AND call_id=$call AND state='REQUESTED'
                            AND plugin_id=$plugin AND plugin_version=$pluginVersion
                            AND capability_id=$capability AND capability_version=$capabilityVersion
                            AND account_id IS $account AND input_hash=$hash
                            AND EXISTS(SELECT 1 FROM plugin_installations installed
                                WHERE installed.owner_principal_id=capability_calls.owner_principal_id
                                    AND installed.plugin_id=capability_calls.plugin_id AND installed.plugin_version=capability_calls.plugin_version
                                    AND installed.enabled=1 AND installed.removed=0)
                            AND (account_id IS NULL OR (
                                EXISTS(SELECT 1 FROM connected_accounts connected
                                    WHERE connected.owner_principal_id=capability_calls.owner_principal_id
                                        AND connected.account_id=capability_calls.account_id AND connected.lifecycle='CONNECTED')
                                AND EXISTS(SELECT 1 FROM account_capability_bindings binding
                                    WHERE binding.owner_principal_id=capability_calls.owner_principal_id
                                        AND binding.account_id=capability_calls.account_id
                                        AND binding.plugin_id=capability_calls.plugin_id AND binding.plugin_version=capability_calls.plugin_version
                                        AND binding.capability_id=capability_calls.capability_id AND binding.capability_version=capability_calls.capability_version)
                                AND NOT EXISTS(SELECT 1 FROM json_each($requiredPermissions) required
                                    WHERE NOT EXISTS(SELECT 1 FROM account_permissions permission
                                        WHERE permission.owner_principal_id=capability_calls.owner_principal_id
                                            AND permission.account_id=capability_calls.account_id AND permission.permission=required.value))))
                            AND (job_id IS NULL OR (
                                EXISTS(SELECT 1 FROM jobs job WHERE job.owner_principal_id=capability_calls.owner_principal_id
                                    AND job.job_id=capability_calls.job_id AND job.desired_state='ACTIVE')
                                AND EXISTS(SELECT 1 FROM job_capability_grants grant_row
                                    WHERE grant_row.owner_principal_id=capability_calls.owner_principal_id AND grant_row.job_id=capability_calls.job_id
                                        AND grant_row.capability_id=capability_calls.capability_id AND grant_row.capability_version=capability_calls.capability_version)
                                AND (account_id IS NULL OR EXISTS(SELECT 1 FROM job_account_grants account_grant
                                    WHERE account_grant.owner_principal_id=capability_calls.owner_principal_id
                                        AND account_grant.job_id=capability_calls.job_id AND account_grant.account_id=capability_calls.account_id))))
                            AND (conversation_id IS NULL OR job_id IS NOT NULL OR (
                                EXISTS(SELECT 1 FROM conversation_capability_grants conversation_grant
                                    WHERE conversation_grant.owner_principal_id=capability_calls.owner_principal_id
                                        AND conversation_grant.conversation_id=capability_calls.conversation_id
                                        AND conversation_grant.capability_id=capability_calls.capability_id
                                        AND conversation_grant.capability_version=capability_calls.capability_version)
                                AND (account_id IS NULL OR EXISTS(SELECT 1 FROM conversation_account_grants conversation_account
                                    WHERE conversation_account.owner_principal_id=capability_calls.owner_principal_id
                                        AND conversation_account.conversation_id=capability_calls.conversation_id
                                        AND conversation_account.account_id=capability_calls.account_id))));
                        """;command.Parameters.AddWithValue("$owner",request.OwnerPrincipalId);command.Parameters.AddWithValue("$call",request.ExecutionId);command.Parameters.AddWithValue("$plugin",request.PluginId);command.Parameters.AddWithValue("$pluginVersion",request.PluginVersion);command.Parameters.AddWithValue("$capability",request.CapabilityId);command.Parameters.AddWithValue("$capabilityVersion",request.CapabilityVersion);command.Parameters.AddWithValue("$account",(object?)request.AccountId??DBNull.Value);command.Parameters.AddWithValue("$hash",CapabilityPayloadHash.Compute(request.Input));command.Parameters.AddWithValue("$requiredPermissions",JsonSerializer.Serialize(requiredPermissions));return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false)==1;
        }

    public async Task CompleteCapabilityCallAsync(ExecutionRequest request,CapabilityResult result,DateTimeOffset now,CancellationToken token=default)
    {
        var state=result.Outcome==CapabilityOutcome.Succeeded?"SUCCEEDED":"FAILED";var data=result.Output.GetRawText();
        await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var transaction=(SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await using(var update=connection.CreateCommand()){update.Transaction=transaction;update.CommandText="UPDATE capability_calls SET state=$state,completed_at=$completed,error_code=$error,external_server_id=$serverId,external_server_name=$serverName,external_server_version=$serverVersion,external_tool_name=$tool,version=version+1 WHERE owner_principal_id=$owner AND call_id=$call AND state<>'SUCCEEDED';";update.Parameters.AddWithValue("$state",state);update.Parameters.AddWithValue("$completed",FormatTimestamp(now));update.Parameters.AddWithValue("$error",(object?)result.FailureCode??DBNull.Value);update.Parameters.AddWithValue("$serverId",(object?)result.RuntimeIdentity?.ServerId??DBNull.Value);update.Parameters.AddWithValue("$serverName",(object?)result.RuntimeIdentity?.ServerName??DBNull.Value);update.Parameters.AddWithValue("$serverVersion",(object?)result.RuntimeIdentity?.ServerVersion??DBNull.Value);update.Parameters.AddWithValue("$tool",(object?)result.RuntimeIdentity?.ExternalToolName??DBNull.Value);update.Parameters.AddWithValue("$owner",request.OwnerPrincipalId);update.Parameters.AddWithValue("$call",request.ExecutionId);await update.ExecuteNonQueryAsync(token).ConfigureAwait(false);}
        await using(var insert=connection.CreateCommand()){insert.Transaction=transaction;insert.CommandText="""
            INSERT OR IGNORE INTO capability_results(owner_principal_id,result_id,call_id,summary,data_json,evidence_refs_json,truncated,created_at)
            VALUES($owner,$result,$call,$summary,$data,'[]',0,$created);
            """;insert.Parameters.AddWithValue("$owner",request.OwnerPrincipalId);insert.Parameters.AddWithValue("$result",$"{request.ExecutionId}:result");insert.Parameters.AddWithValue("$call",request.ExecutionId);insert.Parameters.AddWithValue("$summary",result.Outcome==CapabilityOutcome.Succeeded?"Capability completed":result.FailureCode??"Capability failed");insert.Parameters.AddWithValue("$data",data);insert.Parameters.AddWithValue("$created",FormatTimestamp(now));await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);}
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProductCapabilityCall>> ListCapabilityCallsAsync(string owner,string? jobRunId,CancellationToken token=default)
    {
        await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.CommandText="""
            SELECT call_id,execution_id,conversation_id,message_id,job_id,job_run_id,plugin_id,plugin_version,
                   capability_id,capability_version,account_id,input_json,input_hash,state,created_at,completed_at,error_code,version,
                   external_server_id,external_server_name,external_server_version,external_tool_name
            FROM capability_calls WHERE owner_principal_id=$owner AND ($run IS NULL OR job_run_id=$run)
            ORDER BY created_at,call_id LIMIT 100;
            """;command.Parameters.AddWithValue("$owner",owner);command.Parameters.AddWithValue("$run",(object?)jobRunId??DBNull.Value);var values=new List<ProductCapabilityCall>();await using var reader=await command.ExecuteReaderAsync(token).ConfigureAwait(false);while(await reader.ReadAsync(token).ConfigureAwait(false))values.Add(new(owner,reader.GetString(0),reader.GetString(1),ReadNullableString(reader,2),ReadNullableString(reader,3),ReadNullableString(reader,4),ReadNullableString(reader,5),reader.GetString(6),reader.GetString(7),reader.GetString(8),reader.GetString(9),ReadNullableString(reader,10),reader.GetString(11),reader.GetString(12),reader.GetString(13),ParseTimestamp(reader.GetString(14)),ReadNullableTimestamp(reader,15),ReadNullableString(reader,16),reader.GetInt64(17),ReadNullableString(reader,18),ReadNullableString(reader,19),ReadNullableString(reader,20),ReadNullableString(reader,21)));return values.AsReadOnly();
    }

    public async Task<(ProductCapabilityCall Call,ProductCapabilityResult? Result)?> GetCapabilityReceiptAsync(string owner,string callId,CancellationToken token=default)
    {await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.CommandText="""
        SELECT call.execution_id,call.conversation_id,call.message_id,call.job_id,call.job_run_id,call.plugin_id,call.plugin_version,
               call.capability_id,call.capability_version,call.account_id,call.input_json,call.input_hash,call.state,call.created_at,call.completed_at,call.error_code,call.version,
               call.external_server_id,call.external_server_name,call.external_server_version,call.external_tool_name,
               result.result_id,result.summary,result.data_json,result.evidence_refs_json,result.truncated,result.created_at
        FROM capability_calls call LEFT JOIN capability_results result ON result.owner_principal_id=call.owner_principal_id AND result.call_id=call.call_id
        WHERE call.owner_principal_id=$owner AND call.call_id=$call;
        """;command.Parameters.AddWithValue("$owner",owner);command.Parameters.AddWithValue("$call",callId);await using var reader=await command.ExecuteReaderAsync(token).ConfigureAwait(false);if(!await reader.ReadAsync(token).ConfigureAwait(false))return null;var call=new ProductCapabilityCall(owner,callId,reader.GetString(0),ReadNullableString(reader,1),ReadNullableString(reader,2),ReadNullableString(reader,3),ReadNullableString(reader,4),reader.GetString(5),reader.GetString(6),reader.GetString(7),reader.GetString(8),ReadNullableString(reader,9),reader.GetString(10),reader.GetString(11),reader.GetString(12),ParseTimestamp(reader.GetString(13)),ReadNullableTimestamp(reader,14),ReadNullableString(reader,15),reader.GetInt64(16),ReadNullableString(reader,17),ReadNullableString(reader,18),ReadNullableString(reader,19),ReadNullableString(reader,20));ProductCapabilityResult? result=reader.IsDBNull(21)?null:new(owner,reader.GetString(21),callId,reader.GetString(22),reader.GetString(23),JsonSerializer.Deserialize<string[]>(reader.GetString(24))??[],reader.GetBoolean(25),ParseTimestamp(reader.GetString(26)));return(call,result);}

    public async Task<IReadOnlyList<ProductCapabilityResult>> ListCapabilityResultsAsync(string owner,string? jobRunId,CancellationToken token=default)
    {
        await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.CommandText="""
            SELECT result.result_id,result.call_id,result.summary,result.data_json,result.evidence_refs_json,result.truncated,result.created_at
            FROM capability_results result JOIN capability_calls call
              ON call.owner_principal_id=result.owner_principal_id AND call.call_id=result.call_id
            WHERE result.owner_principal_id=$owner AND ($run IS NULL OR call.job_run_id=$run)
            ORDER BY result.created_at,result.result_id LIMIT 100;
            """;command.Parameters.AddWithValue("$owner",owner);command.Parameters.AddWithValue("$run",(object?)jobRunId??DBNull.Value);var values=new List<ProductCapabilityResult>();await using var reader=await command.ExecuteReaderAsync(token).ConfigureAwait(false);while(await reader.ReadAsync(token).ConfigureAwait(false))values.Add(new(owner,reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),JsonSerializer.Deserialize<string[]>(reader.GetString(4))??[],reader.GetBoolean(5),ParseTimestamp(reader.GetString(6))));return values.AsReadOnly();
    }

    public async Task AttachCapabilityEvidenceAsync(string owner,string callId,string evidenceId,CancellationToken token=default)
    {await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.CommandText="UPDATE capability_results SET evidence_refs_json=$evidence WHERE owner_principal_id=$owner AND call_id=$call;";command.Parameters.AddWithValue("$evidence",JsonSerializer.Serialize(new[]{evidenceId}));command.Parameters.AddWithValue("$owner",owner);command.Parameters.AddWithValue("$call",callId);await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);}
}