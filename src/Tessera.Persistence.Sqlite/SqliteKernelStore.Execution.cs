using Microsoft.Data.Sqlite;
using Tessera.Core.Kernel;
using Tessera.Core.Product;

namespace Tessera.Persistence.Sqlite;

public sealed partial class SqliteKernelStore
{
    public async Task AddProposedAsync(
        string ownerPrincipalId,
        ActionRecord action,
        ExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(request);
        EnsureOwner(ownerPrincipalId, action.OwnerPrincipalId);
        EnsureOwner(ownerPrincipalId, request.OwnerPrincipalId);
        if (action.State != ActionState.Proposed || action.Version != 0)
        {
            throw new InvalidOperationException("A new durable action must begin in PROPOSED at version zero.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var actionCommand = connection.CreateCommand())
        {
            actionCommand.Transaction = transaction;
            BindAction(actionCommand, action);
            actionCommand.CommandText = $"INSERT INTO actions({ActionColumns}) VALUES ({ActionParameters});";
            await actionCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var requestCommand = connection.CreateCommand())
        {
            requestCommand.Transaction = transaction;
            requestCommand.CommandText = """
                INSERT INTO durable_execution_requests(
                    owner_principal_id,action_id,execution_id,capability_id,capability_version,
                    plugin_id,plugin_version,account_id,target_scope,target_hash,input_json,idempotency_key,
                    conversation_id,message_id,job_id,job_run_id,created_at)
                VALUES($owner,$action,$execution,$capability,$capabilityVersion,$plugin,$pluginVersion,
                    $account,$target,$targetHash,$input,$idempotency,$conversation,$message,$job,$run,$created);
                """;
            requestCommand.Parameters.AddWithValue("$owner", ownerPrincipalId);
            requestCommand.Parameters.AddWithValue("$action", action.ActionId);
            requestCommand.Parameters.AddWithValue("$execution", request.ExecutionId);
            requestCommand.Parameters.AddWithValue("$capability", request.CapabilityId);
            requestCommand.Parameters.AddWithValue("$capabilityVersion", request.CapabilityVersion);
            requestCommand.Parameters.AddWithValue("$plugin", request.PluginId);
            requestCommand.Parameters.AddWithValue("$pluginVersion", request.PluginVersion);
            requestCommand.Parameters.AddWithValue("$account", (object?)request.AccountId ?? DBNull.Value);
            requestCommand.Parameters.AddWithValue("$target", request.TargetScope);
            requestCommand.Parameters.AddWithValue("$targetHash", request.TargetHash);
            requestCommand.Parameters.AddWithValue("$input", request.Input.GetRawText());
            requestCommand.Parameters.AddWithValue("$idempotency", request.IdempotencyKey);
            requestCommand.Parameters.AddWithValue("$conversation", (object?)request.ConversationId ?? DBNull.Value);
            requestCommand.Parameters.AddWithValue("$message", (object?)request.MessageId ?? DBNull.Value);
            requestCommand.Parameters.AddWithValue("$job", (object?)request.JobId ?? DBNull.Value);
            requestCommand.Parameters.AddWithValue("$run", (object?)request.JobRunId ?? DBNull.Value);
            requestCommand.Parameters.AddWithValue("$created", FormatTimestamp(action.CreatedAt));
            await requestCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    async Task<ExecutionRequest?> IDurableExecutionRequestRepository.GetAsync(
        string ownerPrincipalId,
        string actionId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT execution_id,capability_id,capability_version,plugin_id,plugin_version,account_id,
                target_scope,target_hash,input_json,idempotency_key,conversation_id,message_id,job_id,job_run_id
            FROM durable_execution_requests
            WHERE owner_principal_id=$owner AND action_id=$action;
            """;
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        command.Parameters.AddWithValue("$action", actionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        using var input = System.Text.Json.JsonDocument.Parse(reader.GetString(8));
        return new ExecutionRequest(
            ownerPrincipalId, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), ReadNullableString(reader, 5), reader.GetString(6), reader.GetString(7),
            input.RootElement.Clone(), reader.GetString(9), ReadNullableString(reader, 10), ReadNullableString(reader, 11),
            ReadNullableString(reader, 12), ReadNullableString(reader, 13));
    }

    async Task<(ActionRecord Action,ExecutionRequest Request)?> IDurableExecutionRequestRepository.GetByIdempotencyAsync(string ownerPrincipalId,string idempotencyKey,CancellationToken cancellationToken)
    {
        string? actionId;await using(var connection=await OpenConnectionAsync(cancellationToken).ConfigureAwait(false)){await using var command=connection.CreateCommand();command.CommandText="SELECT action_id FROM actions WHERE owner_principal_id=$owner AND idempotency_key=$key;";command.Parameters.AddWithValue("$owner",ownerPrincipalId);command.Parameters.AddWithValue("$key",idempotencyKey);actionId=await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;}
        if(actionId is null)return null;var action=await GetActionAsync(ownerPrincipalId,actionId,cancellationToken).ConfigureAwait(false);var request=await ((IDurableExecutionRequestRepository)this).GetAsync(ownerPrincipalId,actionId,cancellationToken).ConfigureAwait(false);return action is null||request is null?null:(action,request);
    }

    public async Task AddAsync(
        string ownerPrincipalId,
        ActionRecord action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnsureOwner(ownerPrincipalId, action.OwnerPrincipalId);
        if (action.State != ActionState.Proposed || action.Version != 0)
        {
            throw new InvalidOperationException("A new durable action must begin in PROPOSED at version zero.");
        }
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        BindAction(command, action);
        command.CommandText = $"INSERT INTO actions({ActionColumns}) VALUES ({ActionParameters});";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    Task<ActionRecord?> IActionRepository.GetAsync(
        string ownerPrincipalId,
        string actionId,
        CancellationToken cancellationToken)
        => GetActionAsync(ownerPrincipalId, actionId, cancellationToken);

    public async Task<ActionRecord?> GetActionAsync(
        string ownerPrincipalId,
        string actionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ActionColumns} FROM actions WHERE owner_principal_id = $owner AND action_id = $id;";
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        command.Parameters.AddWithValue("$id", actionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadAction(reader)
            : null;
    }

    public async Task<bool> UpdateAsync(
        string ownerPrincipalId,
        ActionRecord action,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnsureOwner(ownerPrincipalId, action.OwnerPrincipalId);
        if (action.Version != expectedVersion + 1)
        {
            throw new InvalidOperationException("Updated action version must advance exactly once.");
        }

        var current = await GetActionAsync(ownerPrincipalId, action.ActionId, cancellationToken).ConfigureAwait(false);
        if (current is null
            || current.Version != expectedVersion
            || !ActionRecord.CanTransition(current.State, action.State))
        {
            return false;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction=(SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction=transaction;
        BindAction(command, action);
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);
        command.CommandText = """
            UPDATE actions SET
                authorization_ref = $authorizationRef,
                state = $state,
                attempt_count = $attemptCount,
                started_at = $startedAt,
                completed_at = $completedAt,
                provider_receipt = $providerReceipt,
                verification_state = $verificationState,
                failure = $failure,
                account_id = $accountId,
                plugin_id = $pluginId,
                plugin_version = $pluginVersion,
                target_hash = $targetHash,
                expires_at = $expiresAt,
                execution_id = $executionId,
                conversation_id = $conversationId,
                message_id = $messageId,
                job_id = $jobId,
                job_run_id = $jobRunId,
                version = $version
            WHERE owner_principal_id = $owner
              AND action_id = $id
                            AND version = $expectedVersion
                            AND capability_id = $capabilityId
                            AND capability_version = $capabilityVersion
                            AND intent = $intent
                            AND payload_hash = $payloadHash
                            AND target_scope = $targetScope
                            AND risk_class = $riskClass
                            AND policy_decision_ref = $policyDecisionRef
                            AND idempotency_key = $idempotencyKey
                            AND created_at = $createdAt
                            AND schema_version = $schemaVersion;
            """;
        if(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false)!=1)return false;
        if(action.State is ActionState.ExecutionSucceeded or ActionState.ExternallyConfirmed&&action.R2Binding?.ConversationId is { } conversation)
        {
            var messageId=$"action:{action.ActionId}:confirmed";var occurred=action.CompletedAt??DateTimeOffset.UtcNow;
            await using(var message=connection.CreateCommand()){message.Transaction=transaction;message.CommandText="""
                INSERT OR IGNORE INTO messages(owner_principal_id,message_id,conversation_id,role,status,retry_of,created_at,completed_at,version)
                SELECT $owner,$message,$conversation,'SYSTEM_EVENT','COMPLETED',NULL,$occurred,$occurred,1
                FROM conversations WHERE owner_principal_id=$owner AND conversation_id=$conversation;
                """;message.Parameters.AddWithValue("$owner",ownerPrincipalId);message.Parameters.AddWithValue("$message",messageId);message.Parameters.AddWithValue("$conversation",conversation);message.Parameters.AddWithValue("$occurred",FormatTimestamp(occurred));await message.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);}
            await using(var part=connection.CreateCommand()){part.Transaction=transaction;part.CommandText="""
                INSERT OR IGNORE INTO message_parts(owner_principal_id,part_id,message_id,sequence,kind,text,capability_call_id,capability_result_id,action_id,evidence_refs_json,error_code)
                SELECT $owner,$part,$message,1,'ACTION',NULL,NULL,NULL,$action,'[]',NULL
                FROM messages WHERE owner_principal_id=$owner AND message_id=$message;
                """;part.Parameters.AddWithValue("$owner",ownerPrincipalId);part.Parameters.AddWithValue("$part",$"action:{action.ActionId}:part");part.Parameters.AddWithValue("$message",messageId);part.Parameters.AddWithValue("$action",action.ActionId);await part.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);}
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);return true;
    }

    public async Task<IReadOnlyList<ActionRecord>> ListByStateAsync(
        string ownerPrincipalId,
        ActionState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {ActionColumns}
            FROM actions
            WHERE owner_principal_id = $owner AND state = $state
            ORDER BY created_at DESC, action_id;
            """;
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        command.Parameters.AddWithValue("$state", state.ToContractValue());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<ActionRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(ReadAction(reader));
        }

        return records.AsReadOnly();
    }

    public async Task<int> ExpireProposedActionsAsync(DateTimeOffset now,CancellationToken token=default)
    {await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.CommandText="UPDATE actions SET state='EXPIRED',completed_at=$now,failure='approval_expired',version=version+1 WHERE state='PROPOSED' AND expires_at IS NOT NULL AND expires_at<=$now;";command.Parameters.AddWithValue("$now",FormatTimestamp(now));return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);}

    public async Task<int> RecoverStrandedStartedActionsAsync(DateTimeOffset now,TimeSpan grace,CancellationToken token=default)
    {await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.CommandText="UPDATE actions SET state='RECONCILIATION_REQUIRED',completed_at=NULL,failure='started_action_recovered_unknown',version=version+1 WHERE state='STARTED' AND started_at IS NOT NULL AND started_at<=$cutoff;";command.Parameters.AddWithValue("$cutoff",FormatTimestamp(now-grace));return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);}

    public async Task<int> RecoverVerifiedActionsAsync(DateTimeOffset now,CancellationToken token=default)
    {await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.CommandText="UPDATE actions SET state='EXTERNALLY_CONFIRMED',completed_at=$now,version=version+1 WHERE state='PROVIDER_VERIFIED' AND provider_receipt IS NOT NULL AND verification_state IS NOT NULL;";command.Parameters.AddWithValue("$now",FormatTimestamp(now));return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);}

    public async Task AddAsync(
        string ownerPrincipalId,
        ActionAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        EnsureOwner(ownerPrincipalId, authorization.OwnerPrincipalId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO action_authorizations(
                authorization_id, owner_principal_id, capability_id, capability_version,
                action_id, payload_hash, target_scope, issued_at, expires_at, consumed_at,
                account_id,plugin_id,plugin_version,target_hash,execution_id)
            VALUES (
                $id, $owner, $capabilityId, $capabilityVersion,
                $actionId, $payloadHash, $targetScope, $issuedAt, $expiresAt, $consumedAt,
                $accountId,$pluginId,$pluginVersion,$targetHash,$executionId);
            """;
        BindAuthorization(command, authorization);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ActionAuthorization?> GetAuthorizationAsync(
        string ownerPrincipalId,
        string authorizationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT authorization_id, owner_principal_id, capability_id, capability_version,
                     action_id, payload_hash, target_scope, issued_at, expires_at, consumed_at,
                     account_id,plugin_id,plugin_version,target_hash,execution_id
            FROM action_authorizations
            WHERE owner_principal_id = $owner AND authorization_id = $id;
            """;
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        command.Parameters.AddWithValue("$id", authorizationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadAuthorization(reader)
            : null;
    }

    Task<ActionAuthorization?> IActionAuthorizationRepository.GetAsync(string ownerPrincipalId,string authorizationId,CancellationToken cancellationToken)
        =>GetAuthorizationAsync(ownerPrincipalId,authorizationId,cancellationToken);

    public async Task<ActionRecord?> TryConsumeAndAuthorizeAsync(
        string ownerPrincipalId,
        string authorizationId,
        ActionRecord proposedAction,
        DateTimeOffset authorizedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposedAction);
        EnsureOwner(ownerPrincipalId, proposedAction.OwnerPrincipalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationId);
        if (proposedAction.State != ActionState.Proposed)
        {
            throw new InvalidOperationException("Authorization requires a proposed action.");
        }

        var authorized = proposedAction.TransitionTo(
            ActionState.Authorized,
            authorizedAt,
            authorizationId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var consume = connection.CreateCommand();
        consume.Transaction = transaction;
        consume.CommandText = """
            UPDATE action_authorizations
            SET consumed_at = $consumedAt
            WHERE owner_principal_id = $owner
              AND authorization_id = $id
              AND consumed_at IS NULL
              AND issued_at <= $consumedAt
              AND expires_at > $consumedAt
              AND capability_id = $capabilityId
              AND capability_version = $capabilityVersion
              AND action_id = $actionId
              AND payload_hash = $payloadHash
              AND target_scope = $targetScope
              AND account_id IS $accountId
              AND plugin_id IS $pluginId
              AND plugin_version IS $pluginVersion
              AND target_hash IS $targetHash
              AND execution_id IS $executionId
                            AND EXISTS (
                                    SELECT 1 FROM actions candidate
                                    WHERE candidate.owner_principal_id = $owner
                                        AND candidate.action_id = $actionId
                                        AND candidate.state = 'PROPOSED'
                                        AND candidate.version = $expectedVersion
                                        AND candidate.capability_id = $capabilityId
                                        AND candidate.capability_version = $capabilityVersion
                                        AND candidate.payload_hash = $payloadHash
                                        AND candidate.target_scope = $targetScope
                                        AND candidate.account_id IS $accountId
                                        AND candidate.plugin_id IS $pluginId
                                        AND candidate.plugin_version IS $pluginVersion
                                        AND candidate.target_hash IS $targetHash
                                        AND candidate.execution_id IS $executionId
                                        AND (candidate.plugin_id IS NULL OR EXISTS (
                                                SELECT 1 FROM plugin_installations plugin
                                                WHERE plugin.owner_principal_id=candidate.owner_principal_id
                                                    AND plugin.plugin_id=candidate.plugin_id
                                                    AND plugin.plugin_version=candidate.plugin_version
                                                    AND plugin.enabled=1))
                                        AND (candidate.account_id IS NULL OR EXISTS (
                                                SELECT 1 FROM connected_accounts account
                                                WHERE account.owner_principal_id=candidate.owner_principal_id
                                                    AND account.account_id=candidate.account_id
                                                    AND account.lifecycle='CONNECTED'))
                                        AND (candidate.job_id IS NULL OR (
                                                EXISTS (SELECT 1 FROM jobs job
                                                        WHERE job.owner_principal_id=candidate.owner_principal_id
                                                            AND job.job_id=candidate.job_id AND job.desired_state='ACTIVE')
                                                AND EXISTS (SELECT 1 FROM job_capability_grants capability_grant
                                                        WHERE capability_grant.owner_principal_id=candidate.owner_principal_id
                                                            AND capability_grant.job_id=candidate.job_id
                                                            AND capability_grant.capability_id=candidate.capability_id
                                                            AND capability_grant.capability_version=candidate.capability_version)
                                                AND (candidate.account_id IS NULL OR EXISTS (
                                                        SELECT 1 FROM job_account_grants account_grant
                                                        WHERE account_grant.owner_principal_id=candidate.owner_principal_id
                                                            AND account_grant.job_id=candidate.job_id
                                                            AND account_grant.account_id=candidate.account_id))
                                                AND (candidate.risk_class='ReadOnly' OR EXISTS (
                                                        SELECT 1 FROM job_side_effect_grants effect_grant
                                                        WHERE effect_grant.owner_principal_id=candidate.owner_principal_id
                                                            AND effect_grant.job_id=candidate.job_id
                                                            AND effect_grant.side_effect_class=candidate.risk_class))))
                                                    AND (candidate.conversation_id IS NULL OR candidate.job_id IS NOT NULL OR (
                                                        EXISTS(SELECT 1 FROM conversation_capability_grants conversation_grant
                                                            WHERE conversation_grant.owner_principal_id=candidate.owner_principal_id
                                                                AND conversation_grant.conversation_id=candidate.conversation_id
                                                                AND conversation_grant.capability_id=candidate.capability_id
                                                                AND conversation_grant.capability_version=candidate.capability_version)
                                                        AND (candidate.account_id IS NULL OR EXISTS(SELECT 1 FROM conversation_account_grants conversation_account
                                                            WHERE conversation_account.owner_principal_id=candidate.owner_principal_id
                                                                AND conversation_account.conversation_id=candidate.conversation_id
                                                                AND conversation_account.account_id=candidate.account_id))))
              );
            """;
        consume.Parameters.AddWithValue("$consumedAt", FormatTimestamp(authorizedAt));
        consume.Parameters.AddWithValue("$owner", ownerPrincipalId);
        consume.Parameters.AddWithValue("$id", authorizationId);
        consume.Parameters.AddWithValue("$capabilityId", proposedAction.CapabilityId);
        consume.Parameters.AddWithValue("$capabilityVersion", proposedAction.CapabilityVersion);
        consume.Parameters.AddWithValue("$actionId", proposedAction.ActionId);
        consume.Parameters.AddWithValue("$payloadHash", proposedAction.PayloadHash);
        consume.Parameters.AddWithValue("$targetScope", proposedAction.TargetScope);
        consume.Parameters.AddWithValue("$expectedVersion", proposedAction.Version);
        BindR2Predicate(consume, proposedAction.R2Binding);
        if (await consume.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        await using var reserve = connection.CreateCommand();
        reserve.Transaction = transaction;
        reserve.CommandText = """
            UPDATE actions
            SET authorization_ref = $authorizationId,
                state = 'AUTHORIZED',
                version = $nextVersion
            WHERE owner_principal_id = $owner
              AND action_id = $actionId
              AND state = 'PROPOSED'
              AND version = $expectedVersion
              AND capability_id = $capabilityId
              AND capability_version = $capabilityVersion
              AND payload_hash = $payloadHash
              AND target_scope = $targetScope;
            """;
        reserve.Parameters.AddWithValue("$authorizationId", authorizationId);
        reserve.Parameters.AddWithValue("$nextVersion", authorized.Version);
        reserve.Parameters.AddWithValue("$owner", ownerPrincipalId);
        reserve.Parameters.AddWithValue("$actionId", proposedAction.ActionId);
        reserve.Parameters.AddWithValue("$expectedVersion", proposedAction.Version);
        reserve.Parameters.AddWithValue("$capabilityId", proposedAction.CapabilityId);
        reserve.Parameters.AddWithValue("$capabilityVersion", proposedAction.CapabilityVersion);
        reserve.Parameters.AddWithValue("$payloadHash", proposedAction.PayloadHash);
        reserve.Parameters.AddWithValue("$targetScope", proposedAction.TargetScope);
        BindR2Predicate(reserve, proposedAction.R2Binding);
        if (await reserve.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return authorized;
    }

    public async Task<ActionRecord?> TryStartAuthorizedAsync(
        string ownerPrincipalId,
        string actionId,
        long expectedVersion,
        string? authorizationId,
        string capabilityId,
        string capabilityVersion,
        string payloadHash,
        string targetScope,
        string? idempotencyKey,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(authorizationId) || string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return null;
        }

        var persisted = await GetActionAsync(ownerPrincipalId, actionId, cancellationToken).ConfigureAwait(false);
        var binding = persisted?.R2Binding;
        var installation = binding?.PluginId is null || binding.PluginVersion is null
            ? null
            : await GetPluginInstallationAsync(ownerPrincipalId,binding.PluginId,binding.PluginVersion,cancellationToken).ConfigureAwait(false);
        var requiredPermissions = installation is null
            ? []
            : CapabilityContract(installation.ManifestJson,capabilityId,capabilityVersion)?.RequiredPermissions??[];
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE actions
            SET state = 'STARTED',
                attempt_count = attempt_count + 1,
                started_at = $startedAt,
                completed_at = NULL,
                failure = NULL,
                version = version + 1
            WHERE owner_principal_id = $owner
              AND action_id = $actionId
              AND state = 'AUTHORIZED'
              AND version = $expectedVersion
              AND authorization_ref = $authorizationId
              AND capability_id = $capabilityId
              AND capability_version = $capabilityVersion
              AND payload_hash = $payloadHash
              AND target_scope = $targetScope
              AND account_id IS $accountId
              AND plugin_id IS $pluginId
              AND plugin_version IS $pluginVersion
              AND target_hash IS $targetHash
              AND execution_id IS $executionId
                            AND (plugin_id IS NULL OR EXISTS(
                                        SELECT 1 FROM plugin_installations plugin
                                        WHERE plugin.owner_principal_id=actions.owner_principal_id
                                            AND plugin.plugin_id=actions.plugin_id AND plugin.plugin_version=actions.plugin_version
                                            AND plugin.enabled=1 AND plugin.removed=0))
                            AND (account_id IS NULL OR (
                                        EXISTS(SELECT 1 FROM connected_accounts account
                                            WHERE account.owner_principal_id=actions.owner_principal_id
                                                AND account.account_id=actions.account_id AND account.lifecycle='CONNECTED')
                                        AND EXISTS(SELECT 1 FROM account_capability_bindings binding
                                            WHERE binding.owner_principal_id=actions.owner_principal_id
                                                AND binding.account_id=actions.account_id
                                                AND binding.plugin_id=actions.plugin_id AND binding.plugin_version=actions.plugin_version
                                                AND binding.capability_id=actions.capability_id AND binding.capability_version=actions.capability_version)
                                        AND NOT EXISTS(SELECT 1 FROM json_each($requiredPermissions) required
                                            WHERE NOT EXISTS(SELECT 1 FROM account_permissions permission
                                                WHERE permission.owner_principal_id=actions.owner_principal_id
                                                    AND permission.account_id=actions.account_id AND permission.permission=required.value))))
                            AND (job_id IS NULL OR (
                                        EXISTS(SELECT 1 FROM jobs job WHERE job.owner_principal_id=actions.owner_principal_id
                                            AND job.job_id=actions.job_id AND job.desired_state='ACTIVE')
                                        AND EXISTS(SELECT 1 FROM job_capability_grants capability_grant
                                            WHERE capability_grant.owner_principal_id=actions.owner_principal_id
                                                AND capability_grant.job_id=actions.job_id
                                                AND capability_grant.capability_id=actions.capability_id
                                                AND capability_grant.capability_version=actions.capability_version)
                                        AND (account_id IS NULL OR EXISTS(SELECT 1 FROM job_account_grants account_grant
                                            WHERE account_grant.owner_principal_id=actions.owner_principal_id
                                                AND account_grant.job_id=actions.job_id AND account_grant.account_id=actions.account_id))
                                        AND (risk_class='ReadOnly' OR EXISTS(SELECT 1 FROM job_side_effect_grants effect_grant
                                            WHERE effect_grant.owner_principal_id=actions.owner_principal_id
                                                AND effect_grant.job_id=actions.job_id AND effect_grant.side_effect_class=actions.risk_class))))
                            AND (conversation_id IS NULL OR job_id IS NOT NULL OR (
                                        EXISTS(SELECT 1 FROM conversation_capability_grants conversation_grant
                                            WHERE conversation_grant.owner_principal_id=actions.owner_principal_id
                                                AND conversation_grant.conversation_id=actions.conversation_id
                                                AND conversation_grant.capability_id=actions.capability_id
                                                AND conversation_grant.capability_version=actions.capability_version)
                                        AND (account_id IS NULL OR EXISTS(SELECT 1 FROM conversation_account_grants conversation_account
                                            WHERE conversation_account.owner_principal_id=actions.owner_principal_id
                                                AND conversation_account.conversation_id=actions.conversation_id
                                                AND conversation_account.account_id=actions.account_id))))
              AND idempotency_key = $idempotencyKey;
            """;
        command.Parameters.AddWithValue("$startedAt", FormatTimestamp(startedAt));
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        command.Parameters.AddWithValue("$actionId", actionId);
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);
        command.Parameters.AddWithValue("$authorizationId", authorizationId);
        command.Parameters.AddWithValue("$capabilityId", capabilityId);
        command.Parameters.AddWithValue("$capabilityVersion", capabilityVersion);
        command.Parameters.AddWithValue("$payloadHash", payloadHash);
        command.Parameters.AddWithValue("$targetScope", targetScope);
        command.Parameters.AddWithValue("$idempotencyKey", idempotencyKey);
        command.Parameters.AddWithValue("$requiredPermissions", System.Text.Json.JsonSerializer.Serialize(requiredPermissions));
        BindR2Predicate(command, persisted?.R2Binding);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            return null;
        }

        return await ReadActionOnConnectionAsync(
            connection,
            ownerPrincipalId,
            actionId,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task AddAsync(
        string ownerPrincipalId,
        WorkflowCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        EnsureOwner(ownerPrincipalId, checkpoint.OwnerPrincipalId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        BindWorkflow(command, checkpoint);
        command.CommandText = """
            INSERT INTO workflow_checkpoints(
                workflow_id, owner_principal_id, workflow_type, state, current_step,
                input_refs_json, output_refs_json, wake_condition, created_at, updated_at, version)
            VALUES (
                $id, $owner, $workflowType, $state, $currentStep,
                $inputRefs, $outputRefs, $wakeCondition, $createdAt, $updatedAt, $version);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    Task<WorkflowCheckpoint?> IWorkflowRepository.GetAsync(
        string ownerPrincipalId,
        string workflowId,
        CancellationToken cancellationToken)
        => GetWorkflowAsync(ownerPrincipalId, workflowId, cancellationToken);

    public async Task<WorkflowCheckpoint?> GetWorkflowAsync(
        string ownerPrincipalId,
        string workflowId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT workflow_id, owner_principal_id, workflow_type, state, current_step,
                   input_refs_json, output_refs_json, wake_condition, created_at, updated_at, version
            FROM workflow_checkpoints
            WHERE owner_principal_id = $owner AND workflow_id = $id;
            """;
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        command.Parameters.AddWithValue("$id", workflowId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadWorkflow(reader)
            : null;
    }

    async Task<bool> IWorkflowRepository.UpdateAsync(
        string ownerPrincipalId,
        WorkflowCheckpoint checkpoint,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        EnsureOwner(ownerPrincipalId, checkpoint.OwnerPrincipalId);
        if (checkpoint.Version != expectedVersion + 1)
        {
            throw new InvalidOperationException("Updated workflow version must advance exactly once.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        BindWorkflow(command, checkpoint);
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);
        command.CommandText = """
            UPDATE workflow_checkpoints SET
                workflow_type = $workflowType,
                state = $state,
                current_step = $currentStep,
                input_refs_json = $inputRefs,
                output_refs_json = $outputRefs,
                wake_condition = $wakeCondition,
                created_at = $createdAt,
                updated_at = $updatedAt,
                version = $version
            WHERE owner_principal_id = $owner
              AND workflow_id = $id
              AND version = $expectedVersion;
            """;
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private const string ActionColumns = """
        action_id, owner_principal_id, capability_id, capability_version, intent,
        payload_hash, target_scope, risk_class, policy_decision_ref, authorization_ref,
        state, idempotency_key, attempt_count, created_at, started_at, completed_at,
        provider_receipt, verification_state, failure, schema_version, version
        , account_id, plugin_id, plugin_version, target_hash, expires_at, execution_id,
        conversation_id, message_id, job_id, job_run_id
        """;

    private const string ActionParameters = """
        $id, $owner, $capabilityId, $capabilityVersion, $intent,
        $payloadHash, $targetScope, $riskClass, $policyDecisionRef, $authorizationRef,
        $state, $idempotencyKey, $attemptCount, $createdAt, $startedAt, $completedAt,
        $providerReceipt, $verificationState, $failure, $schemaVersion, $version
        , $accountId, $pluginId, $pluginVersion, $targetHash, $expiresAt, $executionId,
        $conversationId, $messageId, $jobId, $jobRunId
        """;

    private static void BindAction(SqliteCommand command, ActionRecord action)
    {
        command.Parameters.AddWithValue("$id", action.ActionId);
        command.Parameters.AddWithValue("$owner", action.OwnerPrincipalId);
        command.Parameters.AddWithValue("$capabilityId", action.CapabilityId);
        command.Parameters.AddWithValue("$capabilityVersion", action.CapabilityVersion);
        command.Parameters.AddWithValue("$intent", action.Intent);
        command.Parameters.AddWithValue("$payloadHash", action.PayloadHash);
        command.Parameters.AddWithValue("$targetScope", action.TargetScope);
        command.Parameters.AddWithValue("$riskClass", action.RiskClass);
        command.Parameters.AddWithValue("$policyDecisionRef", action.PolicyDecisionRef);
        command.Parameters.AddWithValue("$authorizationRef", (object?)action.AuthorizationRef ?? DBNull.Value);
        command.Parameters.AddWithValue("$state", action.State.ToContractValue());
        command.Parameters.AddWithValue("$idempotencyKey", action.IdempotencyKey);
        command.Parameters.AddWithValue("$attemptCount", action.AttemptCount);
        command.Parameters.AddWithValue("$createdAt", FormatTimestamp(action.CreatedAt));
        command.Parameters.AddWithValue("$startedAt", action.StartedAt is null
            ? DBNull.Value
            : FormatTimestamp(action.StartedAt.Value));
        command.Parameters.AddWithValue("$completedAt", action.CompletedAt is null
            ? DBNull.Value
            : FormatTimestamp(action.CompletedAt.Value));
        command.Parameters.AddWithValue("$providerReceipt", (object?)action.ProviderReceipt ?? DBNull.Value);
        command.Parameters.AddWithValue("$verificationState", (object?)action.VerificationState ?? DBNull.Value);
        command.Parameters.AddWithValue("$failure", (object?)action.Failure ?? DBNull.Value);
        command.Parameters.AddWithValue("$schemaVersion", action.SchemaVersion);
        command.Parameters.AddWithValue("$version", action.Version);
        BindR2Values(command, action.R2Binding);
    }

    private static ActionRecord ReadAction(SqliteDataReader reader)
        => ActionRecord.Create(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            ReadNullableString(reader, 9),
            ParseActionState(reader.GetString(10)),
            reader.GetString(11),
            reader.GetInt32(12),
            ParseTimestamp(reader.GetString(13)),
            ReadNullableTimestamp(reader, 14),
            ReadNullableTimestamp(reader, 15),
            ReadNullableString(reader, 16),
            ReadNullableString(reader, 17),
            ReadNullableString(reader, 18),
            reader.GetInt32(19),
            reader.GetInt64(20)) with
        {
            R2Binding = reader.IsDBNull(22) ? null : new ActionR2Binding(
                ReadNullableString(reader, 21), reader.GetString(22), reader.GetString(23), reader.GetString(24),
                ParseTimestamp(reader.GetString(25)), reader.GetString(26), ReadNullableString(reader, 27),
                ReadNullableString(reader, 28), ReadNullableString(reader, 29), ReadNullableString(reader, 30)),
        };

    private static async Task<ActionRecord?> ReadActionOnConnectionAsync(
        SqliteConnection connection,
        string ownerPrincipalId,
        string actionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {ActionColumns} FROM actions WHERE owner_principal_id = $owner AND action_id = $id;";
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        command.Parameters.AddWithValue("$id", actionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadAction(reader)
            : null;
    }

    private static ActionState ParseActionState(string value) => value switch
    {
        "PROPOSED" => ActionState.Proposed,
        "AUTHORIZED" => ActionState.Authorized,
        "STARTED" => ActionState.Started,
        "EXECUTION_SUCCEEDED" => ActionState.ExecutionSucceeded,
        "PROVIDER_VERIFIED" => ActionState.ProviderVerified,
        "EXTERNALLY_CONFIRMED" => ActionState.ExternallyConfirmed,
        "FAILED" => ActionState.Failed,
        "CANCELED" => ActionState.Canceled,
        "EXPIRED" => ActionState.Expired,
        "RECONCILIATION_REQUIRED" => ActionState.ReconciliationRequired,
        _ => throw new InvalidDataException($"Unknown action state '{value}'."),
    };

    private static void BindAuthorization(SqliteCommand command, ActionAuthorization authorization)
    {
        command.Parameters.AddWithValue("$id", authorization.AuthorizationId);
        command.Parameters.AddWithValue("$owner", authorization.OwnerPrincipalId);
        command.Parameters.AddWithValue("$capabilityId", authorization.CapabilityId);
        command.Parameters.AddWithValue("$capabilityVersion", authorization.CapabilityVersion);
        command.Parameters.AddWithValue("$actionId", authorization.ActionId);
        command.Parameters.AddWithValue("$payloadHash", authorization.PayloadHash);
        command.Parameters.AddWithValue("$targetScope", authorization.TargetScope);
        command.Parameters.AddWithValue("$issuedAt", FormatTimestamp(authorization.IssuedAt));
        command.Parameters.AddWithValue("$expiresAt", FormatTimestamp(authorization.ExpiresAt));
        command.Parameters.AddWithValue("$consumedAt", authorization.ConsumedAt is null
            ? DBNull.Value
            : FormatTimestamp(authorization.ConsumedAt.Value));
        BindR2Predicate(command, authorization.R2Binding);
    }

    private static ActionAuthorization ReadAuthorization(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            ParseTimestamp(reader.GetString(7)),
            ParseTimestamp(reader.GetString(8)),
            ReadNullableTimestamp(reader, 9),
            reader.IsDBNull(11) ? null : new ActionR2Binding(
                ReadNullableString(reader, 10), reader.GetString(11), reader.GetString(12), reader.GetString(13),
                ParseTimestamp(reader.GetString(8)), reader.GetString(14)));

    private static void BindR2Values(SqliteCommand command, ActionR2Binding? binding)
    {
        BindR2Predicate(command, binding);
        command.Parameters.AddWithValue("$expiresAt", binding is null ? DBNull.Value : FormatTimestamp(binding.ExpiresAt));
        command.Parameters.AddWithValue("$conversationId", (object?)binding?.ConversationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$messageId", (object?)binding?.MessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$jobId", (object?)binding?.JobId ?? DBNull.Value);
        command.Parameters.AddWithValue("$jobRunId", (object?)binding?.JobRunId ?? DBNull.Value);
    }

    private static void BindR2Predicate(SqliteCommand command, ActionR2Binding? binding)
    {
        command.Parameters.AddWithValue("$accountId", (object?)binding?.AccountId ?? DBNull.Value);
        command.Parameters.AddWithValue("$pluginId", (object?)binding?.PluginId ?? DBNull.Value);
        command.Parameters.AddWithValue("$pluginVersion", (object?)binding?.PluginVersion ?? DBNull.Value);
        command.Parameters.AddWithValue("$targetHash", (object?)binding?.TargetHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$executionId", (object?)binding?.ExecutionId ?? DBNull.Value);
    }

    private static void BindWorkflow(SqliteCommand command, WorkflowCheckpoint checkpoint)
    {
        command.Parameters.AddWithValue("$id", checkpoint.WorkflowId);
        command.Parameters.AddWithValue("$owner", checkpoint.OwnerPrincipalId);
        command.Parameters.AddWithValue("$workflowType", checkpoint.WorkflowType);
        command.Parameters.AddWithValue("$state", checkpoint.State);
        command.Parameters.AddWithValue("$currentStep", checkpoint.CurrentStep);
        command.Parameters.AddWithValue("$inputRefs", Serialize(checkpoint.InputRefs));
        command.Parameters.AddWithValue("$outputRefs", Serialize(checkpoint.OutputRefs));
        command.Parameters.AddWithValue("$wakeCondition", (object?)checkpoint.WakeCondition ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", FormatTimestamp(checkpoint.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", FormatTimestamp(checkpoint.UpdatedAt));
        command.Parameters.AddWithValue("$version", checkpoint.Version);
    }

    private static WorkflowCheckpoint ReadWorkflow(SqliteDataReader reader)
        => WorkflowCheckpoint.Create(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            DeserializeReferences(reader.GetString(5)),
            DeserializeReferences(reader.GetString(6)),
            ReadNullableString(reader, 7),
            ParseTimestamp(reader.GetString(8)),
            ParseTimestamp(reader.GetString(9)),
            reader.GetInt64(10));
}