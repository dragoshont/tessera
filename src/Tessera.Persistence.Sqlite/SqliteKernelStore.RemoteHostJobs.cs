using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Tessera.Core.Product;

namespace Tessera.Persistence.Sqlite;

internal sealed record HostDispatchPreparationResult(
    HostWorkLease? Lease,
    JobRunBlocker? Blocker,
    bool RoutedToHost);

internal sealed record RemoteHostDispatchCandidate(
    RemoteHost Host,
    HostCapabilityAdvertisement Capability,
    HostCapabilityGrant CapabilityGrant,
    IReadOnlyList<HostResourceGrantTuple> ResourceTuples,
    string ResourceGrantHash);

public sealed record JobExecutionPolicyReceiptMutation(
    ProductIdempotencyReceipt? Receipt,
    bool Replayed,
    string? Error)
{
    public bool Succeeded => Error is null;
}

public sealed partial class SqliteKernelStore
{
    private const string HostProfileId = "host.repo.identity@1";
    private const int RemoteOutputLimitBytes = 32 * 1024;
    private const int RemoteEventLimit = 50;
    private const int RemoteEventSummaryLimit = 512;
    private const int RemoteEventDataLimitBytes = 16 * 1024;

    public async Task<JobExecutionPolicy?> GetJobExecutionPolicyAsync(
        string owner,
        string jobId,
        CancellationToken token = default)
    {
        await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
        return await ReadJobExecutionPolicyAsync(connection, null, owner, jobId, token).ConfigureAwait(false);
    }

    public async Task<JobExecutionPolicy?> PutJobExecutionPolicyAsync(
        JobExecutionPolicy policy,
        long expectedVersion,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.Version != expectedVersion + 1)
            throw new InvalidOperationException("Execution policy version must advance exactly once.");
        RemoteHostValidation.ValidateExecutionPolicy(
            policy.Location,
            policy.PreferredHostId,
            policy.RequiredCapabilities,
            policy.RequiredResourceIds,
            policy.FallbackPolicy);

        await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        if (policy.Location != JobExecutionLocations.Server
            && !await HostPolicyMatchesActiveJobAsync(connection, transaction, policy, token).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(token).ConfigureAwait(false);
            return null;
        }
        var current = await ReadJobExecutionPolicyAsync(
            connection, transaction, policy.OwnerPrincipalId, policy.JobId, token).ConfigureAwait(false);
        if (current is null)
        {
            if (expectedVersion != 0)
            {
                await transaction.RollbackAsync(token).ConfigureAwait(false);
                return null;
            }

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO job_execution_policies(
                    owner_principal_id,job_id,location,preferred_host_id,
                    required_capabilities_json,required_resource_ids_json,fallback_policy,version)
                SELECT $owner,$job,$location,$host,$capabilities,$resources,$fallback,$version
                WHERE EXISTS(
                    SELECT 1 FROM jobs WHERE owner_principal_id=$owner AND job_id=$job);
                """;
            BindExecutionPolicy(insert, policy);
            if (await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(token).ConfigureAwait(false);
                return null;
            }
        }
        else
        {
            if (current.Version != expectedVersion)
            {
                await transaction.RollbackAsync(token).ConfigureAwait(false);
                return null;
            }

            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE job_execution_policies
                SET location=$location,
                    preferred_host_id=$host,
                    required_capabilities_json=$capabilities,
                    required_resource_ids_json=$resources,
                    fallback_policy=$fallback,
                    version=$version
                WHERE owner_principal_id=$owner AND job_id=$job AND version=$expectedVersion;
                """;
            BindExecutionPolicy(update, policy);
            update.Parameters.AddWithValue("$expectedVersion", expectedVersion);
            if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(token).ConfigureAwait(false);
                return null;
            }
        }

        await transaction.CommitAsync(token).ConfigureAwait(false);
        return policy;
    }

    public async Task<JobExecutionPolicyReceiptMutation> PutJobExecutionPolicyWithReceiptAsync(
        JobExecutionPolicy policy,
        long expectedVersion,
        string idempotencyKey,
        string requestHash,
        DateTimeOffset now,
        CancellationToken token = default)
    {
        const string routeFamily = "job-execution-policy-put";
        ArgumentNullException.ThrowIfNull(policy);
        RemoteHostValidation.ValidateIdentifier(idempotencyKey, nameof(idempotencyKey));
        RemoteHostValidation.ValidateLowerHex(requestHash, 64, nameof(requestHash));
        requestHash = BindTargetRequestHash(policy.JobId, requestHash);
        if (policy.Version != expectedVersion + 1)
            throw new ArgumentException("Execution policy version must advance exactly once.", nameof(policy));
        RemoteHostValidation.ValidateExecutionPolicy(
            policy.Location,
            policy.PreferredHostId,
            policy.RequiredCapabilities,
            policy.RequiredResourceIds,
            policy.FallbackPolicy);

        await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var prior = await ReadReceiptAsync(
            connection, transaction, policy.OwnerPrincipalId, routeFamily, idempotencyKey, token).ConfigureAwait(false);
        if (prior is not null)
        {
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return string.Equals(prior.RequestHash, requestHash, StringComparison.Ordinal)
                ? new(prior, true, null)
                : new(null, false, "idempotency_conflict");
        }

        if (policy.Location != JobExecutionLocations.Server
            && !await HostPolicyMatchesActiveJobAsync(connection, transaction, policy, token).ConfigureAwait(false))
        {
            return await RejectJobExecutionPolicyAsync(
                connection, transaction, policy.OwnerPrincipalId, policy.JobId, routeFamily,
                idempotencyKey, requestHash, "job_capability_not_granted", now, token).ConfigureAwait(false);
        }

        var current = await ReadJobExecutionPolicyAsync(
            connection, transaction, policy.OwnerPrincipalId, policy.JobId, token).ConfigureAwait(false);
        if (current is null && expectedVersion != 0 || current is not null && current.Version != expectedVersion)
        {
            return await RejectJobExecutionPolicyAsync(
                connection, transaction, policy.OwnerPrincipalId, policy.JobId, routeFamily,
                idempotencyKey, requestHash, "version_conflict", now, token).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = current is null
            ? """
                INSERT INTO job_execution_policies(
                    owner_principal_id,job_id,location,preferred_host_id,
                    required_capabilities_json,required_resource_ids_json,fallback_policy,version)
                SELECT $owner,$job,$location,$host,$capabilities,$resources,$fallback,$version
                WHERE EXISTS(SELECT 1 FROM jobs WHERE owner_principal_id=$owner AND job_id=$job);
                """
            : """
                UPDATE job_execution_policies
                SET location=$location,preferred_host_id=$host,
                    required_capabilities_json=$capabilities,required_resource_ids_json=$resources,
                    fallback_policy=$fallback,version=$version
                WHERE owner_principal_id=$owner AND job_id=$job AND version=$expectedVersion;
                """;
        BindExecutionPolicy(command, policy);
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);
        if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
        {
            return await RejectJobExecutionPolicyAsync(
                connection, transaction, policy.OwnerPrincipalId, policy.JobId, routeFamily,
                idempotencyKey, requestHash, "version_conflict", now, token).ConfigureAwait(false);
        }

        var receipt = CreateRemoteHostReceipt(
            policy.OwnerPrincipalId, routeFamily, idempotencyKey, requestHash, 200,
            SerializeExecutionPolicy(policy), "job_execution_policy", policy.JobId, now);
        await CommitRemoteHostMutationAsync(connection, transaction, receipt, token).ConfigureAwait(false);
        return new(receipt, false, null);
    }

    public async Task<bool> DeleteJobExecutionPolicyAsync(
        string owner,
        string jobId,
        long expectedVersion,
        CancellationToken token = default)
    {
        await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM job_execution_policies
            WHERE owner_principal_id=$owner AND job_id=$job AND version=$expectedVersion;
            """;
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$job", jobId);
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);
        return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
    }

    public async Task<JobExecutionPolicyReceiptMutation> DeleteJobExecutionPolicyWithReceiptAsync(
        string owner,
        string jobId,
        long expectedVersion,
        string idempotencyKey,
        string requestHash,
        DateTimeOffset now,
        CancellationToken token = default)
    {
        const string routeFamily = "job-execution-policy-delete";
        RemoteHostValidation.ValidateIdentifier(jobId, nameof(jobId));
        RemoteHostValidation.ValidateIdentifier(idempotencyKey, nameof(idempotencyKey));
        RemoteHostValidation.ValidateLowerHex(requestHash, 64, nameof(requestHash));
        requestHash = BindTargetRequestHash(jobId, requestHash);
        await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var prior = await ReadReceiptAsync(
            connection, transaction, owner, routeFamily, idempotencyKey, token).ConfigureAwait(false);
        if (prior is not null)
        {
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return string.Equals(prior.RequestHash, requestHash, StringComparison.Ordinal)
                ? new(prior, true, null)
                : new(null, false, "idempotency_conflict");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM job_execution_policies
            WHERE owner_principal_id=$owner AND job_id=$job AND version=$expectedVersion;
            """;
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$job", jobId);
        command.Parameters.AddWithValue("$expectedVersion", expectedVersion);
        if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
        {
            return await RejectJobExecutionPolicyAsync(
                connection, transaction, owner, jobId, routeFamily, idempotencyKey,
                requestHash, "version_conflict", now, token).ConfigureAwait(false);
        }

        var receipt = CreateRemoteHostReceipt(
            owner, routeFamily, idempotencyKey, requestHash, 200,
            SerializeDefaultExecutionPolicy(jobId), "job_execution_policy", jobId, now);
        await CommitRemoteHostMutationAsync(connection, transaction, receipt, token).ConfigureAwait(false);
        return new(receipt, false, null);
    }

    public async Task<RemoteJobRunProjection?> GetRemoteJobRunProjectionAsync(
        string owner,
        string runId,
        CancellationToken token = default)
    {
        await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
        var run = await GetJobRunAsync(owner, runId, token).ConfigureAwait(false);
        if (run is null)
            return null;
        var blocker = await ReadActiveJobRunBlockerAsync(connection, null, owner, runId, token).ConfigureAwait(false);
        var lease = await ReadLatestLeaseByRunAsync(connection, null, owner, runId, token).ConfigureAwait(false);
        var host = lease is null ? null : await ReadHostByIdAsync(connection, null, owner, lease.HostId, token).ConfigureAwait(false);
        var checkpoints = await ListJobRunCheckpointsAsync(owner, runId, token).ConfigureAwait(false);
        return new(blocker, lease, host, checkpoints);
    }

    internal async Task<HostDispatchPreparationResult> PrepareHostDispatchAsync(
        ProductJob job,
        ProductJobRun run,
        long fence,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(run);
        await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var policy = await ReadJobExecutionPolicyAsync(
            connection, transaction, job.OwnerPrincipalId, job.JobId, token).ConfigureAwait(false);
        if (policy is null || policy.Location == JobExecutionLocations.Server)
        {
            await transaction.RollbackAsync(token).ConfigureAwait(false);
            return new(null, null, false);
        }
        var currentRun = await ReadJobRunAsync(connection, transaction, run.OwnerPrincipalId, run.RunId, token).ConfigureAwait(false);
        if (currentRun is null || currentRun.State != "QUEUED" || currentRun.JobId != policy.JobId
            || !await HostPolicyMatchesActiveJobAsync(connection, transaction, policy, token).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(token).ConfigureAwait(false);
            return new(null, null, true);
        }

        if (!await HasActiveSchedulerFenceAsync(connection, transaction, run.OwnerPrincipalId, run.RunId, fence, now, token).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(token).ConfigureAwait(false);
            return new(null, null, true);
        }

        var selection = await SelectDispatchCandidateAsync(connection, transaction, policy, now, token).ConfigureAwait(false);
        if (selection.Candidate is null)
        {
            var blocker = await UpsertJobRunBlockerAsync(
                connection,
                transaction,
                run.OwnerPrincipalId,
                run.RunId,
                selection.BlockerCode,
                policy.PreferredHostId,
                selection.CapabilityId,
                selection.ResourceId,
                selection.DetailCode,
                now,
                token).ConfigureAwait(false);
            await ReleaseSchedulerLeaseAsync(connection, transaction, run.OwnerPrincipalId, run.RunId, fence, now, token)
                .ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return new(null, blocker, true);
        }

        var candidate = selection.Candidate;
        var leaseId = $"lease-{Guid.NewGuid():N}";
        var attempt = await NextLeaseAttemptAsync(connection, transaction, run.OwnerPrincipalId, run.RunId, token).ConfigureAwait(false);
        var inputHash = ComputeRemoteInputHash(candidate.ResourceTuples.Select(item => item.ResourceId).ToArray());
        var lease = new HostWorkLease(
            run.OwnerPrincipalId,
            leaseId,
            run.RunId,
            run.JobId,
            candidate.Host.HostId,
            fence,
            attempt,
            HostProfileId,
            candidate.Capability.CapabilityId,
            candidate.Capability.CapabilityVersion,
            candidate.CapabilityGrant.Version,
            inputHash,
            HostLeaseStates.Offered,
            now,
            now.Add(leaseDuration),
            null,
            null,
            null,
            null,
            null,
            null,
            1);

        await InsertLeaseAsync(connection, transaction, lease, candidate.ResourceTuples, token).ConfigureAwait(false);
        await ClearActiveJobRunBlockerAsync(connection, transaction, run.OwnerPrincipalId, run.RunId, now, token)
            .ConfigureAwait(false);
        await SetHostLifecycleAsync(connection, transaction, run.OwnerPrincipalId, candidate.Host.HostId, RemoteHostLifecycles.Busy, now, token)
            .ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return new(lease, null, true);
    }

    private static async Task<bool> HostPolicyMatchesActiveJobAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JobExecutionPolicy policy,
        CancellationToken token)
    {
        var capability = policy.RequiredCapabilities.Single();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM jobs job
            WHERE job.owner_principal_id=$owner AND job.job_id=$job AND job.desired_state='ACTIVE'
              AND EXISTS(
                  SELECT 1 FROM job_capability_grants grant_row
                  WHERE grant_row.owner_principal_id=job.owner_principal_id
                    AND grant_row.job_id=job.job_id
                    AND grant_row.capability_id=$capability
                    AND grant_row.capability_version=$capabilityVersion);
            """;
        command.Parameters.AddWithValue("$owner", policy.OwnerPrincipalId);
        command.Parameters.AddWithValue("$job", policy.JobId);
        command.Parameters.AddWithValue("$capability", capability.Id);
        command.Parameters.AddWithValue("$capabilityVersion", capability.Version);
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not null;
    }

    public async Task<int> RecoverExpiredHostLeasesAsync(DateTimeOffset now, CancellationToken token = default)
    {
        await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var leases = await ReadExpiredActiveLeasesAsync(connection, transaction, now, token).ConfigureAwait(false);
        var changed = 0;
        foreach (var lease in leases)
        {
            if (lease.State == HostLeaseStates.Offered)
            {
                await SetLeaseStateAsync(connection, transaction, lease, HostLeaseStates.Expired, null, null, null, now, token)
                    .ConfigureAwait(false);
                changed += 1;
                await UpdateRunForRequeueAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId, lease.SchedulerFence, now, token)
                    .ConfigureAwait(false);
                await UpsertJobRunBlockerAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId,
                    JobRunBlockerCodes.WaitingForHost, lease.HostId, null, null, "lease_expired", now, token)
                    .ConfigureAwait(false);
                await ReleaseSchedulerLeaseAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId, lease.SchedulerFence, now, token)
                    .ConfigureAwait(false);
            }
            else
            {
                await SetLeaseStateAsync(connection, transaction, lease, HostLeaseStates.ReconciliationRequired, HostLeaseCompletionOutcomes.Unknown, null, "lease_expired", now, token)
                    .ConfigureAwait(false);
                changed += 1;
                await UpdateRunForReconciliationAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId, lease.SchedulerFence, now, "host_lease_expired_unknown", token)
                    .ConfigureAwait(false);
                await UpsertJobRunBlockerAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId,
                    JobRunBlockerCodes.HostDisconnected, lease.HostId, null, null, "lease_expired", now, token)
                    .ConfigureAwait(false);
                await ReleaseSchedulerLeaseAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId, lease.SchedulerFence, now, token)
                    .ConfigureAwait(false);
            }

            await RestoreHostLifecycleIfIdleAsync(connection, transaction, lease.OwnerPrincipalId, lease.HostId, now, token)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(token).ConfigureAwait(false);
        return changed;
    }

    internal static async Task RevokeActiveHostLeasesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string owner,
        string hostId,
        DateTimeOffset now,
        CancellationToken token)
    {
        var leases = await ReadActiveLeasesForHostAsync(connection, transaction, owner, hostId, token).ConfigureAwait(false);
        foreach (var lease in leases)
        {
            await SetLeaseStateAsync(connection, transaction, lease, HostLeaseStates.Revoked, lease.Outcome, null, "host_revoked", now, token)
                .ConfigureAwait(false);
            if (lease.State == HostLeaseStates.Offered)
            {
                await UpdateRunForRequeueAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId, lease.SchedulerFence, now, token)
                    .ConfigureAwait(false);
                await UpsertJobRunBlockerAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId,
                    JobRunBlockerCodes.WaitingForHost, lease.HostId, null, null, "host_revoked", now, token)
                    .ConfigureAwait(false);
            }
            else
            {
                await UpdateRunForReconciliationAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId, lease.SchedulerFence, now, "host_revoked_unknown", token)
                    .ConfigureAwait(false);
                await UpsertJobRunBlockerAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId,
                    JobRunBlockerCodes.HostUpdateRequired, lease.HostId, null, null, "host_revoked", now, token)
                    .ConfigureAwait(false);
            }

            await ReleaseSchedulerLeaseAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId, lease.SchedulerFence, now, token)
                .ConfigureAwait(false);
        }
    }

    internal static async Task InvalidateActiveHostLeasesForGrantChangeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string owner,
        string hostId,
        DateTimeOffset now,
        CancellationToken token)
    {
        var leases = await ReadActiveLeasesForHostAsync(
            connection, transaction, owner, hostId, token).ConfigureAwait(false);
        foreach (var lease in leases)
        {
            if (await LeaseGrantsAreCurrentAsync(connection, transaction, lease, token).ConfigureAwait(false))
                continue;
            if (lease.State == HostLeaseStates.Offered)
            {
                await SetLeaseStateAsync(connection, transaction, lease, HostLeaseStates.Revoked,
                    null, null, "host_grant_changed", now, token).ConfigureAwait(false);
                await UpdateRunForRequeueAsync(connection, transaction, lease.OwnerPrincipalId,
                    lease.RunId, lease.SchedulerFence, now, token).ConfigureAwait(false);
                await UpsertJobRunBlockerAsync(connection, transaction, lease.OwnerPrincipalId,
                    lease.RunId, JobRunBlockerCodes.WaitingForHost, lease.HostId, null, null,
                    "host_grant_changed", now, token).ConfigureAwait(false);
            }
            else
            {
                await SetLeaseStateAsync(connection, transaction, lease,
                    HostLeaseStates.ReconciliationRequired, HostLeaseCompletionOutcomes.Unknown,
                    null, "host_grant_changed", now, token).ConfigureAwait(false);
                await UpdateRunForReconciliationAsync(connection, transaction, lease.OwnerPrincipalId,
                    lease.RunId, lease.SchedulerFence, now, "host_grant_changed", token).ConfigureAwait(false);
                await UpsertJobRunBlockerAsync(connection, transaction, lease.OwnerPrincipalId,
                    lease.RunId, JobRunBlockerCodes.HostUpdateRequired, lease.HostId, null, null,
                    "host_grant_changed", now, token).ConfigureAwait(false);
            }
            await ReleaseSchedulerLeaseAsync(connection, transaction, lease.OwnerPrincipalId,
                lease.RunId, lease.SchedulerFence, now, token).ConfigureAwait(false);
        }
    }

    internal static async Task CancelActiveHostLeasesForJobAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string owner,
        string jobId,
        string desiredState,
        DateTimeOffset now,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = HostLeaseSelect + " WHERE owner_principal_id=$owner AND job_id=$job AND state IN ('OFFERED','ACKNOWLEDGED','RUNNING','DISCONNECTED');";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$job", jobId);
        var leases = new List<HostWorkLease>();
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(token).ConfigureAwait(false)) leases.Add(ReadLease(reader));
        }

        foreach (var lease in leases)
        {
            await SetLeaseStateAsync(connection, transaction, lease, HostLeaseStates.Revoked,
                lease.Outcome, null, desiredState == "CANCELED" ? "job_canceled" : "job_paused", now, token)
                .ConfigureAwait(false);
            if (lease.State is HostLeaseStates.Acknowledged or HostLeaseStates.Running or HostLeaseStates.Disconnected)
            {
                await UpdateRunForReconciliationAsync(connection, transaction, lease.OwnerPrincipalId,
                    lease.RunId, lease.SchedulerFence, now,
                    desiredState == "CANCELED" ? "job_canceled_host_outcome_unknown" : "job_paused_host_outcome_unknown",
                    token).ConfigureAwait(false);
                await UpsertJobRunBlockerAsync(connection, transaction, lease.OwnerPrincipalId,
                    lease.RunId, JobRunBlockerCodes.HostDisconnected, lease.HostId, null, null,
                    desiredState == "CANCELED" ? "job_canceled" : "job_paused", now, token)
                    .ConfigureAwait(false);
            }
            await ReleaseSchedulerLeaseAsync(connection, transaction, lease.OwnerPrincipalId,
                lease.RunId, lease.SchedulerFence, now, token).ConfigureAwait(false);
            await RestoreHostLifecycleIfIdleAsync(connection, transaction, lease.OwnerPrincipalId,
                lease.HostId, now, token).ConfigureAwait(false);
        }
    }

    internal static async Task<HostMessageBusinessResponse> PollHostAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RemoteHost host,
        int maxWaitSeconds,
        HostPollActiveAttempt? activeAttempt,
        DateTimeOffset now,
        CancellationToken token)
    {
        return await GuardHostRequestAsync(async () =>
        {
            if (maxWaitSeconds is < 1 or > 25)
                return HostLeaseProblem(400, "host_invalid_request");

            if (activeAttempt is not null)
            {
                RemoteHostValidation.ValidateIdentifier(activeAttempt.LeaseId, nameof(activeAttempt));
                RemoteHostValidation.ValidateIdentifier(activeAttempt.LocalAttemptId, nameof(activeAttempt));
                if (!HostPollAttemptStates.IsValid(activeAttempt.State))
                    return HostLeaseProblem(400, "host_invalid_request");
            }

            var lease = await ReadLatestLeaseForHostAsync(connection, transaction, host.OwnerPrincipalId, host.HostId, token).ConfigureAwait(false);
            if (lease is null || lease.ExecuteUntil <= now || lease.State is HostLeaseStates.Completed or HostLeaseStates.Failed or HostLeaseStates.ReconciliationRequired or HostLeaseStates.Declined or HostLeaseStates.Expired or HostLeaseStates.Revoked)
            {
                return EmptyPollResponse(now);
            }

            if (!await LeaseGrantsAreCurrentAsync(connection, transaction, lease, token).ConfigureAwait(false))
            {
                await InvalidateActiveHostLeasesForGrantChangeAsync(
                    connection, transaction, host.OwnerPrincipalId, host.HostId, now, token).ConfigureAwait(false);
                await RestoreHostLifecycleIfIdleAsync(
                    connection, transaction, host.OwnerPrincipalId, host.HostId, now, token).ConfigureAwait(false);
                return HostLeaseProblem(409, "host_lease_grant_changed");
            }

            if (lease.State == HostLeaseStates.Offered)
            {
                if (activeAttempt is not null)
                    return HostLeaseProblem(409, "host_attempt_mismatch");
                return await PollLeaseResponseAsync(connection, transaction, lease, now, includeCommand: true, token)
                    .ConfigureAwait(false);
            }

            if (activeAttempt is null
                || lease.LeaseId != activeAttempt.LeaseId
                || lease.LocalAttemptId != activeAttempt.LocalAttemptId)
                return HostLeaseProblem(409, "host_attempt_mismatch");

            if (activeAttempt.State == HostPollAttemptStates.Completed)
            {
                if (lease.State != HostLeaseStates.Running)
                    return HostLeaseProblem(409, "host_lease_invalid");
                return await PollLeaseResponseAsync(connection, transaction, lease, now, includeCommand: false, token)
                    .ConfigureAwait(false);
            }

            var reconciliation = await ReconcileHostLeaseAsync(
                connection,
                transaction,
                host,
                lease.LeaseId,
                lease.Version,
                activeAttempt.LocalAttemptId,
                activeAttempt.State,
                null,
                now,
                token).ConfigureAwait(false);
            if (reconciliation.ResponseStatus >= 400)
                return reconciliation;
            var reconciledLease = await ReadLeaseAsync(
                connection, transaction, lease.OwnerPrincipalId, lease.LeaseId, token).ConfigureAwait(false);
            return reconciledLease is null || reconciledLease.State is HostLeaseStates.Expired or HostLeaseStates.ReconciliationRequired
                ? EmptyPollResponse(now)
                : await PollLeaseResponseAsync(connection, transaction, reconciledLease, now, includeCommand: false, token)
                    .ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    internal static async Task<HostMessageBusinessResponse> AcknowledgeHostLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RemoteHost host,
        string leaseId,
        long leaseVersion,
        string localAttemptId,
        bool accepted,
        string? rejectionCode,
        DateTimeOffset now,
        CancellationToken token)
    {
        return await GuardHostRequestAsync(async () =>
        {
            RemoteHostValidation.ValidateIdentifier(leaseId, nameof(leaseId));
            RemoteHostValidation.ValidateIdentifier(localAttemptId, nameof(localAttemptId));
            if (accepted)
            {
                if (rejectionCode is not null)
                    return HostLeaseProblem(400, "host_invalid_request");
            }
            else if (rejectionCode is not null)
            {
                RemoteHostValidation.ValidateIdentifier(rejectionCode, nameof(rejectionCode));
            }

            var lease = await ReadLeaseAsync(connection, transaction, host.OwnerPrincipalId, leaseId, token).ConfigureAwait(false);
            if (lease is null || lease.HostId != host.HostId)
                return HostLeaseProblem(409, "host_lease_invalid");
            if (lease.Version != leaseVersion || lease.State != HostLeaseStates.Offered || lease.ExecuteUntil <= now)
                return HostLeaseProblem(409, lease.ExecuteUntil <= now ? "host_lease_expired" : "host_lease_invalid");
            if (!await LeaseGrantsAreCurrentAsync(connection, transaction, lease, token).ConfigureAwait(false))
                return HostLeaseProblem(409, "host_lease_grant_changed");

            if (!accepted)
            {
                await SetLeaseStateAsync(connection, transaction, lease, HostLeaseStates.Declined, null, null, rejectionCode, now, token)
                    .ConfigureAwait(false);
                await UpdateRunForRequeueAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId, lease.SchedulerFence, now, token)
                    .ConfigureAwait(false);
                await UpsertJobRunBlockerAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId,
                    JobRunBlockerCodes.WaitingForHost, host.HostId, null, null, rejectionCode ?? "host_declined", now, token)
                    .ConfigureAwait(false);
                await ReleaseSchedulerLeaseAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId, lease.SchedulerFence, now, token)
                    .ConfigureAwait(false);
                await RestoreHostLifecycleIfIdleAsync(connection, transaction, lease.OwnerPrincipalId, host.HostId, now, token)
                    .ConfigureAwait(false);
                return new(200, RemoteHostSnapshotSerializer.SerializeVersion(lease.Version + 1));
            }

            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE host_work_leases
                    SET state='ACKNOWLEDGED',acknowledged_at=$acknowledgedAt,local_attempt_id=$localAttemptId,version=version+1
                    WHERE owner_principal_id=$owner AND lease_id=$lease AND version=$version AND state='OFFERED';
                    """;
                update.Parameters.AddWithValue("$acknowledgedAt", FormatTimestamp(now));
                update.Parameters.AddWithValue("$localAttemptId", localAttemptId);
                update.Parameters.AddWithValue("$owner", lease.OwnerPrincipalId);
                update.Parameters.AddWithValue("$lease", lease.LeaseId);
                update.Parameters.AddWithValue("$version", lease.Version);
                if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                    return HostLeaseProblem(409, "host_lease_invalid");
            }

            await ClearActiveJobRunBlockerAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId, now, token)
                .ConfigureAwait(false);
            await StartRunOnConnectionAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId, lease.SchedulerFence, now, token)
                .ConfigureAwait(false);
            return new(200, RemoteHostSnapshotSerializer.SerializeVersion(lease.Version + 1));
        }).ConfigureAwait(false);
    }

    internal static async Task<HostMessageBusinessResponse> AppendHostLeaseEventsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RemoteHost host,
        string leaseId,
        long leaseVersion,
        string localAttemptId,
        IReadOnlyList<HostLeaseEvent> events,
        DateTimeOffset now,
        CancellationToken token)
    {
        return await GuardHostRequestAsync(async () =>
        {
            var leaseValidation = await ValidateLeaseMutationAsync(
                connection,
                transaction,
                host,
                leaseId,
                leaseVersion,
                localAttemptId,
                now,
                requireUnexpired: true,
                requireCurrentGrants: true,
                requireRunningRun: false,
                allowedStates: [HostLeaseStates.Acknowledged, HostLeaseStates.Running],
                token).ConfigureAwait(false);
            if (leaseValidation.Problem is not null)
                return leaseValidation.Problem;

            var lease = leaseValidation.Lease!;
            var nextSequence = await ReadNextLeaseEventSequenceAsync(connection, transaction, lease.OwnerPrincipalId, lease.LeaseId, token).ConfigureAwait(false);
            var validatedEvents = ValidateLeaseEventBatch(lease, events, nextSequence, now);
            foreach (var item in validatedEvents)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO host_lease_events(owner_principal_id,lease_id,event_id,sequence,type,occurred_at,summary,data_json)
                    VALUES($owner,$lease,$eventId,$sequence,$type,$occurredAt,$summary,$dataJson);
                    """;
                insert.Parameters.AddWithValue("$owner", lease.OwnerPrincipalId);
                insert.Parameters.AddWithValue("$lease", lease.LeaseId);
                insert.Parameters.AddWithValue("$eventId", item.EventId);
                insert.Parameters.AddWithValue("$sequence", item.Sequence);
                insert.Parameters.AddWithValue("$type", item.Type);
                insert.Parameters.AddWithValue("$occurredAt", FormatTimestamp(item.OccurredAt));
                insert.Parameters.AddWithValue("$summary", (object?)item.Summary ?? DBNull.Value);
                insert.Parameters.AddWithValue("$dataJson", (object?)item.DataJson ?? DBNull.Value);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                if (item.Type == HostLeaseEventTypes.StepStarted)
                {
                    await SetLeaseStateAsync(connection, transaction, lease, HostLeaseStates.Running, null, null, null, now, token)
                        .ConfigureAwait(false);
                    lease = lease with { State = HostLeaseStates.Running, Version = lease.Version + 1 };
                }
            }

            return new(200, RemoteHostSnapshotSerializer.SerializeVersion(lease.Version));
        }).ConfigureAwait(false);
    }

    internal static async Task<HostMessageBusinessResponse> CompleteHostLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RemoteHost host,
        string leaseId,
        long leaseVersion,
        string localAttemptId,
        string outcome,
        string? output,
        string? outputSha256,
        bool truncated,
        DateTimeOffset now,
        CancellationToken token)
    {
        return await GuardHostRequestAsync(async () =>
        {
            if (!HostLeaseCompletionOutcomes.IsValid(outcome))
                return HostLeaseProblem(400, "host_invalid_request");
            var leaseValidation = await ValidateLeaseMutationAsync(
                connection,
                transaction,
                host,
                leaseId,
                leaseVersion,
                localAttemptId,
                now,
                requireUnexpired: true,
                requireCurrentGrants: true,
                requireRunningRun: true,
                allowedStates: [HostLeaseStates.Running],
                token).ConfigureAwait(false);
            if (leaseValidation.Problem is not null)
                return leaseValidation.Problem;

            var lease = leaseValidation.Lease!;
            string? normalizedOutput = null;
            string? normalizedOutputHash = null;
            if (!string.IsNullOrWhiteSpace(output))
            {
                if (outputSha256 is null)
                    return HostLeaseProblem(400, "host_invalid_request");
                RemoteHostValidation.ValidateLowerHex(outputSha256, 64, nameof(outputSha256));
                var normalized = RemoteHostOutputNormalizer.Normalize(
                    Encoding.UTF8.GetBytes(output), RemoteOutputLimitBytes);
                normalizedOutput = normalized.Text;
                normalizedOutputHash = normalized.Sha256;
                if (truncated != normalized.Truncated
                    || !string.Equals(outputSha256, normalizedOutputHash, StringComparison.Ordinal))
                    return HostLeaseProblem(409, "host_output_hash_mismatch");
            }
            else if (outputSha256 is not null || truncated)
            {
                return HostLeaseProblem(400, "host_invalid_request");
            }

            var nextLeaseState = outcome switch
            {
                HostLeaseCompletionOutcomes.Succeeded => HostLeaseStates.Completed,
                HostLeaseCompletionOutcomes.Failed => HostLeaseStates.Failed,
                _ => HostLeaseStates.ReconciliationRequired,
            };
            await SetLeaseStateAsync(connection, transaction, lease, nextLeaseState, outcome, normalizedOutputHash, null, now, token)
                .ConfigureAwait(false);
            if (normalizedOutput is not null)
            {
                await InsertOrIgnoreHostOutputAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId, normalizedOutput, truncated, now, token)
                    .ConfigureAwait(false);
            }

            switch (outcome)
            {
                case HostLeaseCompletionOutcomes.Succeeded:
                    await CompleteRunOnConnectionAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId, lease.SchedulerFence, "SUCCEEDED", null, now, token)
                        .ConfigureAwait(false);
                    await ClearActiveJobRunBlockerAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId, now, token)
                        .ConfigureAwait(false);
                    break;
                case HostLeaseCompletionOutcomes.Failed:
                    await CompleteRunOnConnectionAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId, lease.SchedulerFence, "FAILED", "host_failed", now, token)
                        .ConfigureAwait(false);
                    await ClearActiveJobRunBlockerAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId, now, token)
                        .ConfigureAwait(false);
                    break;
                default:
                    await UpdateRunForReconciliationAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId, lease.SchedulerFence, now, "host_unknown_outcome", token)
                        .ConfigureAwait(false);
                    await UpsertJobRunBlockerAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId,
                        JobRunBlockerCodes.HostDisconnected, host.HostId, null, null, "host_unknown_outcome", now, token)
                        .ConfigureAwait(false);
                    break;
            }

            await ReleaseSchedulerLeaseAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId, lease.SchedulerFence, now, token)
                .ConfigureAwait(false);
            await RestoreHostLifecycleIfIdleAsync(connection, transaction, lease.OwnerPrincipalId, host.HostId, now, token)
                .ConfigureAwait(false);
            var run = await ReadJobRunAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId, token).ConfigureAwait(false);
            var updatedLease = await ReadLeaseAsync(connection, transaction, lease.OwnerPrincipalId, lease.LeaseId, token).ConfigureAwait(false);
            return new(200, JsonSerializer.Serialize(new
            {
                lease = updatedLease is null ? null : LeaseDto(updatedLease, await ReadLeaseResourcesAsync(connection, transaction, updatedLease.OwnerPrincipalId, updatedLease.LeaseId, token).ConfigureAwait(false)),
                run = run is null ? null : new { runId = run.RunId, state = run.State, errorCode = run.ErrorCode, version = run.Version },
                replayed = false,
            }));
        }).ConfigureAwait(false);
    }

    internal static async Task<HostMessageBusinessResponse> ReconcileHostLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RemoteHost host,
        string leaseId,
        long leaseVersion,
        string localAttemptId,
        string observedState,
        string? observedOutputSha256,
        DateTimeOffset now,
        CancellationToken token)
    {
        return await GuardHostRequestAsync(async () =>
        {
            if (observedState is not ("NOT_STARTED" or "STARTED" or "UNKNOWN"))
                return HostLeaseProblem(400, "host_invalid_request");
            if (observedOutputSha256 is not null)
                RemoteHostValidation.ValidateLowerHex(observedOutputSha256, 64, nameof(observedOutputSha256));
            if (observedState == "NOT_STARTED" && observedOutputSha256 is not null)
                return HostLeaseProblem(400, "host_invalid_request");
            var lease = await ReadLeaseAsync(connection, transaction, host.OwnerPrincipalId, leaseId, token).ConfigureAwait(false);
            if (lease is null || lease.HostId != host.HostId || lease.Version != leaseVersion || lease.LocalAttemptId != localAttemptId)
                return HostLeaseProblem(409, "host_attempt_mismatch");
            if (lease.State is HostLeaseStates.Completed
                or HostLeaseStates.Failed
                or HostLeaseStates.Declined
                or HostLeaseStates.Expired
                or HostLeaseStates.Revoked
                or HostLeaseStates.ReconciliationRequired)
            {
                return HostLeaseProblem(409, "host_lease_invalid");
            }
            if (!await LeaseGrantsAreCurrentAsync(connection, transaction, lease, token).ConfigureAwait(false))
            {
                await InvalidateActiveHostLeasesForGrantChangeAsync(
                    connection, transaction, host.OwnerPrincipalId, host.HostId, now, token).ConfigureAwait(false);
                await RestoreHostLifecycleIfIdleAsync(
                    connection, transaction, host.OwnerPrincipalId, host.HostId, now, token).ConfigureAwait(false);
                return HostLeaseProblem(409, "host_lease_grant_changed");
            }

            string resolution;
            if (observedState == "NOT_STARTED")
            {
                if (lease.ExecuteUntil > now
                    && lease.State == HostLeaseStates.Acknowledged
                    && !await HasAcceptedLeaseEventAsync(connection, transaction, lease.OwnerPrincipalId, lease.LeaseId, HostLeaseEventTypes.StepStarted, token).ConfigureAwait(false))
                {
                    await SetLeaseStateAsync(connection, transaction, lease, HostLeaseStates.Expired, null, null, "reconciled_not_started", now, token)
                        .ConfigureAwait(false);
                    await UpdateRunForRequeueAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId, lease.SchedulerFence, now, token)
                        .ConfigureAwait(false);
                    await UpsertJobRunBlockerAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId,
                        JobRunBlockerCodes.WaitingForHost, host.HostId, null, null, "reconciled_not_started", now, token)
                        .ConfigureAwait(false);
                    await ReleaseSchedulerLeaseAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId, lease.SchedulerFence, now, token)
                        .ConfigureAwait(false);
                    await RestoreHostLifecycleIfIdleAsync(connection, transaction, lease.OwnerPrincipalId, host.HostId, now, token)
                        .ConfigureAwait(false);
                    resolution = "REQUEUED";
                }
                else
                {
                    return await MoveLeaseToReconciliationRequiredAsync(
                        connection,
                        transaction,
                        host,
                        lease,
                        now,
                        "reconciled_not_started_conflict",
                        "host_reconciliation_required",
                        releaseSchedulerLease: true,
                        resolution: "RECONCILIATION_REQUIRED",
                        token).ConfigureAwait(false);
                }
            }
            else if (observedState == "STARTED")
            {
                if (lease.ExecuteUntil <= now)
                {
                    return await MoveLeaseToReconciliationRequiredAsync(
                        connection,
                        transaction,
                        host,
                        lease,
                        now,
                        "reconciled_started_expired",
                        "host_lease_expired_unknown",
                        releaseSchedulerLease: true,
                        resolution: "RECONCILIATION_REQUIRED",
                        token).ConfigureAwait(false);
                }
                if (lease.State is not (HostLeaseStates.Acknowledged or HostLeaseStates.Running or HostLeaseStates.Disconnected))
                    return HostLeaseProblem(409, "host_lease_invalid");
                if (!await ResumeLeaseRunAsync(connection, transaction, lease, now, token).ConfigureAwait(false))
                    return HostLeaseProblem(409, "host_lease_invalid");
                if (lease.State != HostLeaseStates.Running)
                {
                    await SetLeaseStateAsync(connection, transaction, lease, HostLeaseStates.Running, null, null, null, now, token)
                        .ConfigureAwait(false);
                }
                await ClearActiveJobRunBlockerAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId, now, token)
                    .ConfigureAwait(false);
                resolution = "RESUME";
            }
            else
            {
                if (lease.ExecuteUntil <= now)
                {
                    return await MoveLeaseToReconciliationRequiredAsync(
                        connection,
                        transaction,
                        host,
                        lease,
                        now,
                        "reconciled_unknown_expired",
                        "host_lease_expired_unknown",
                        releaseSchedulerLease: true,
                        resolution: "RECONCILIATION_REQUIRED",
                        token).ConfigureAwait(false);
                }
                if (lease.State is not (HostLeaseStates.Acknowledged or HostLeaseStates.Running or HostLeaseStates.Disconnected))
                    return HostLeaseProblem(409, "host_lease_invalid");
                if (!await ResumeLeaseRunAsync(connection, transaction, lease, now, token).ConfigureAwait(false))
                    return HostLeaseProblem(409, "host_lease_invalid");
                if (lease.State != HostLeaseStates.Disconnected)
                {
                    await SetLeaseStateAsync(connection, transaction, lease, HostLeaseStates.Disconnected, null, observedOutputSha256, "reconciled_unknown", now, token)
                        .ConfigureAwait(false);
                }
                await UpsertJobRunBlockerAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId,
                    JobRunBlockerCodes.HostDisconnected, host.HostId, null, null, "reconciled_unknown", now, token)
                    .ConfigureAwait(false);
                resolution = "WAITING";
            }

            var run = await ReadJobRunAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId, token).ConfigureAwait(false);
            var updatedLease = await ReadLeaseAsync(connection, transaction, lease.OwnerPrincipalId, lease.LeaseId, token).ConfigureAwait(false);
            return new(200, JsonSerializer.Serialize(new
            {
                resolution,
                lease = updatedLease is null ? null : LeaseDto(updatedLease, await ReadLeaseResourcesAsync(connection, transaction, updatedLease.OwnerPrincipalId, updatedLease.LeaseId, token).ConfigureAwait(false)),
                run = run is null ? null : new { runId = run.RunId, state = run.State, errorCode = run.ErrorCode, version = run.Version },
            }));
        }).ConfigureAwait(false);
    }

    private static void BindExecutionPolicy(SqliteCommand command, JobExecutionPolicy policy)
    {
        command.Parameters.AddWithValue("$owner", policy.OwnerPrincipalId);
        command.Parameters.AddWithValue("$job", policy.JobId);
        command.Parameters.AddWithValue("$location", policy.Location);
        command.Parameters.AddWithValue("$host", (object?)policy.PreferredHostId ?? DBNull.Value);
        command.Parameters.AddWithValue("$capabilities", JsonSerializer.Serialize(policy.RequiredCapabilities.Select(item => new
        {
            capabilityId = item.Id,
            capabilityVersion = item.Version,
        }).ToArray()));
        command.Parameters.AddWithValue("$resources", JsonSerializer.Serialize(policy.RequiredResourceIds.ToArray()));
        command.Parameters.AddWithValue("$fallback", policy.FallbackPolicy);
        command.Parameters.AddWithValue("$version", policy.Version);
    }

    private async Task<JobExecutionPolicyReceiptMutation> RejectJobExecutionPolicyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string owner,
        string jobId,
        string routeFamily,
        string idempotencyKey,
        string requestHash,
        string error,
        DateTimeOffset now,
        CancellationToken token)
    {
        var receipt = CreateRemoteHostReceipt(
            owner,
            routeFamily,
            idempotencyKey,
            requestHash,
            409,
            RemoteHostSnapshotSerializer.SerializeProblem(409, error),
            "job_execution_policy",
            jobId,
            now);
        await CommitRemoteHostMutationAsync(connection, transaction, receipt, token).ConfigureAwait(false);
        return new(receipt, false, error);
    }

    private static string SerializeExecutionPolicy(JobExecutionPolicy policy)
        => JsonSerializer.Serialize(new
        {
            jobId = policy.JobId,
            location = policy.Location,
            preferredHostId = policy.PreferredHostId,
            requiredCapabilities = policy.RequiredCapabilities.Select(item => new
            {
                capabilityId = item.Id,
                capabilityVersion = item.Version,
            }).ToArray(),
            requiredResourceIds = policy.RequiredResourceIds,
            fallbackPolicy = policy.FallbackPolicy,
            version = policy.Version,
        });

    private static string SerializeDefaultExecutionPolicy(string jobId)
        => JsonSerializer.Serialize(new
        {
            jobId,
            location = JobExecutionLocations.Server,
            preferredHostId = (string?)null,
            requiredCapabilities = Array.Empty<object>(),
            requiredResourceIds = Array.Empty<string>(),
            fallbackPolicy = JobExecutionFallbackPolicies.None,
            version = 0,
        });

    private static async Task<JobExecutionPolicy?> ReadJobExecutionPolicyAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string owner,
        string jobId,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT location,preferred_host_id,required_capabilities_json,required_resource_ids_json,fallback_policy,version
            FROM job_execution_policies
            WHERE owner_principal_id=$owner AND job_id=$job;
            """;
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$job", jobId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false))
            return null;
        var capabilities = JsonSerializer.Deserialize<ExecutionCapabilityJson[]>(reader.GetString(2)) ?? [];
        var resources = JsonSerializer.Deserialize<string[]>(reader.GetString(3)) ?? [];
        return new(
            owner,
            jobId,
            reader.GetString(0),
            ReadNullableString(reader, 1),
            capabilities.Select(item => (item.CapabilityId, item.CapabilityVersion)).ToArray(),
            resources,
            reader.GetString(4),
            reader.GetInt64(5));
    }

    private static async Task<(RemoteHostDispatchCandidate? Candidate, string BlockerCode, string? CapabilityId, string? ResourceId, string? DetailCode)> SelectDispatchCandidateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JobExecutionPolicy policy,
        DateTimeOffset now,
        CancellationToken token)
    {
        var hostIds = await ListDispatchCandidateHostIdsAsync(connection, transaction, policy, token)
            .ConfigureAwait(false);
        if (hostIds.Count == 0)
            return (null, JobRunBlockerCodes.WaitingForHost, null, null, "no_active_host");

        var missingCapability = false;
        var missingResource = false;
        foreach (var hostId in hostIds)
        {
            var detail = await ReadHostDetailAsync(connection, transaction, policy.OwnerPrincipalId, hostId, token).ConfigureAwait(false);
            if (detail is null)
                continue;
            var capabilityRequirement = policy.RequiredCapabilities.SingleOrDefault();
            var capabilityGrant = detail.CapabilityGrants
                .Where(item => item.RevokedAt is null)
                .SingleOrDefault(item => item.CapabilityId == capabilityRequirement.Id && item.CapabilityVersion == capabilityRequirement.Version);
            var capability = detail.Capabilities.SingleOrDefault(item => item.CapabilityId == capabilityRequirement.Id && item.CapabilityVersion == capabilityRequirement.Version);
            if (capabilityGrant is null || capability is null)
            {
                missingCapability = true;
                continue;
            }

            var tuples = new List<HostResourceGrantTuple>();
            var allResources = true;
            foreach (var resourceId in policy.RequiredResourceIds.OrderBy(item => item, StringComparer.Ordinal))
            {
                var resource = detail.Resources.SingleOrDefault(item => item.ResourceId == resourceId && item.State == RemoteHostValidation.Available);
                var grant = detail.ResourceGrants.SingleOrDefault(item => item.ResourceId == resourceId && item.RevokedAt is null);
                if (resource is null || grant is null)
                {
                    allResources = false;
                    missingResource = true;
                    break;
                }

                tuples.Add(new(resource.ResourceId, grant.Version, grant.AccessMode, resource.Fingerprint));
            }

            if (!allResources)
                continue;

            var hash = RemoteHostValidation.ComputeHostResourceGrantHash(tuples);
            return (new(detail.Host, capability, capabilityGrant, tuples, hash), JobRunBlockerCodes.WaitingForHost, null, null, null);
        }

        var missingCapabilityId = policy.RequiredCapabilities.Count > 0 ? policy.RequiredCapabilities[0].Id : null;
        var missingResourceId = policy.RequiredResourceIds.Count > 0 ? policy.RequiredResourceIds[0] : null;
        return missingCapability
            ? (null, JobRunBlockerCodes.WaitingForCapability, missingCapabilityId, null, "capability_missing")
            : (null, missingResource ? JobRunBlockerCodes.WaitingForResource : JobRunBlockerCodes.WaitingForHost, null, missingResourceId, missingResource ? "resource_missing" : "no_active_host");
    }

    private static async Task<IReadOnlyList<string>> ListDispatchCandidateHostIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JobExecutionPolicy policy,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT host_id
            FROM remote_hosts
            WHERE owner_principal_id=$owner
              AND lifecycle IN ('ONLINE','DEGRADED')
                            AND ($location<>'HOST' OR host_id=$preferred)
            ORDER BY CASE WHEN host_id=$preferred THEN 0 ELSE 1 END, paired_at, host_id;
            """;
                command.Parameters.AddWithValue("$owner", policy.OwnerPrincipalId);
                command.Parameters.AddWithValue("$location", policy.Location);
                command.Parameters.AddWithValue("$preferred", (object?)policy.PreferredHostId ?? DBNull.Value);
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            values.Add(reader.GetString(0));
        return values;
    }

    private static async Task<long> NextLeaseAttemptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string owner,
        string runId,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(attempt),0)+1 FROM host_work_leases WHERE owner_principal_id=$owner AND run_id=$run;";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$run", runId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task InsertLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        HostWorkLease lease,
        IReadOnlyList<HostResourceGrantTuple> resources,
        CancellationToken token)
    {
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO host_work_leases(
                    owner_principal_id,lease_id,run_id,job_id,host_id,scheduler_fence,attempt,profile_id,
                    capability_id,capability_version,capability_grant_version,input_hash,state,issued_at,
                    execute_until,acknowledged_at,completed_at,local_attempt_id,outcome,output_sha256,
                    failure_code,version)
                VALUES($owner,$lease,$run,$job,$host,$fence,$attempt,$profile,
                    $capabilityId,$capabilityVersion,$capabilityGrantVersion,$inputHash,$state,$issuedAt,
                    $executeUntil,NULL,NULL,NULL,NULL,NULL,NULL,$version);
                """;
            command.Parameters.AddWithValue("$owner", lease.OwnerPrincipalId);
            command.Parameters.AddWithValue("$lease", lease.LeaseId);
            command.Parameters.AddWithValue("$run", lease.RunId);
            command.Parameters.AddWithValue("$job", lease.JobId);
            command.Parameters.AddWithValue("$host", lease.HostId);
            command.Parameters.AddWithValue("$fence", lease.SchedulerFence);
            command.Parameters.AddWithValue("$attempt", lease.Attempt);
            command.Parameters.AddWithValue("$profile", lease.ProfileId);
            command.Parameters.AddWithValue("$capabilityId", lease.CapabilityId);
            command.Parameters.AddWithValue("$capabilityVersion", lease.CapabilityVersion);
            command.Parameters.AddWithValue("$capabilityGrantVersion", lease.CapabilityGrantVersion);
            command.Parameters.AddWithValue("$inputHash", lease.InputHash);
            command.Parameters.AddWithValue("$state", lease.State);
            command.Parameters.AddWithValue("$issuedAt", FormatTimestamp(lease.IssuedAt));
            command.Parameters.AddWithValue("$executeUntil", FormatTimestamp(lease.ExecuteUntil));
            command.Parameters.AddWithValue("$version", lease.Version);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        foreach (var resource in resources)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO host_lease_resources(owner_principal_id,lease_id,resource_id,resource_grant_version,access_mode,fingerprint)
                VALUES($owner,$lease,$resourceId,$grantVersion,$accessMode,$fingerprint);
                """;
            command.Parameters.AddWithValue("$owner", lease.OwnerPrincipalId);
            command.Parameters.AddWithValue("$lease", lease.LeaseId);
            command.Parameters.AddWithValue("$resourceId", resource.ResourceId);
            command.Parameters.AddWithValue("$grantVersion", resource.ResourceGrantVersion);
            command.Parameters.AddWithValue("$accessMode", resource.AccessMode);
            command.Parameters.AddWithValue("$fingerprint", resource.Fingerprint);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }

    private static async Task<JobRunBlocker?> UpsertJobRunBlockerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string owner,
        string runId,
        string code,
        string? hostId,
        string? capabilityId,
        string? resourceId,
        string? detailCode,
        DateTimeOffset observedAt,
        CancellationToken token)
    {
        if (!JobRunBlockerCodes.IsValid(code))
            throw new ArgumentException("Blocker code is not supported.", nameof(code));
        var current = await ReadActiveJobRunBlockerAsync(connection, transaction, owner, runId, token).ConfigureAwait(false);
        if (current is not null
            && current.Code == code
            && current.HostId == hostId
            && current.CapabilityId == capabilityId
            && current.ResourceId == resourceId
            && current.DetailCode == detailCode)
        {
            return current;
        }

        await ClearActiveJobRunBlockerAsync(connection, transaction, owner, runId, observedAt, token).ConfigureAwait(false);
        await using var versionCommand = connection.CreateCommand();
        versionCommand.Transaction = transaction;
        versionCommand.CommandText = "SELECT COALESCE(MAX(version),0)+1 FROM job_run_blockers WHERE owner_principal_id=$owner AND run_id=$run;";
        versionCommand.Parameters.AddWithValue("$owner", owner);
        versionCommand.Parameters.AddWithValue("$run", runId);
        var version = Convert.ToInt64(
            await versionCommand.ExecuteScalarAsync(token).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO job_run_blockers(owner_principal_id,run_id,code,host_id,capability_id,resource_id,detail_code,observed_at,cleared_at,version)
            VALUES($owner,$run,$code,$host,$capability,$resource,$detail,$observed,NULL,$version);
            """;
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$code", code);
        command.Parameters.AddWithValue("$host", (object?)hostId ?? DBNull.Value);
        command.Parameters.AddWithValue("$capability", (object?)capabilityId ?? DBNull.Value);
        command.Parameters.AddWithValue("$resource", (object?)resourceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$detail", (object?)detailCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$observed", FormatTimestamp(observedAt));
        command.Parameters.AddWithValue("$version", version);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        return new(owner, runId, code, hostId, capabilityId, resourceId, detailCode, observedAt, null, version);
    }

    private static async Task ClearActiveJobRunBlockerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string owner,
        string runId,
        DateTimeOffset clearedAt,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE job_run_blockers
            SET cleared_at=$clearedAt
            WHERE owner_principal_id=$owner AND run_id=$run AND cleared_at IS NULL;
            """;
        command.Parameters.AddWithValue("$clearedAt", FormatTimestamp(clearedAt));
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$run", runId);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task<JobRunBlocker?> ReadActiveJobRunBlockerAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string owner,
        string runId,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT code,host_id,capability_id,resource_id,detail_code,observed_at,cleared_at,version
            FROM job_run_blockers
            WHERE owner_principal_id=$owner AND run_id=$run AND cleared_at IS NULL
            ORDER BY version DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$run", runId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false)
            ? new(owner, runId, reader.GetString(0), ReadNullableString(reader, 1), ReadNullableString(reader, 2),
                ReadNullableString(reader, 3), ReadNullableString(reader, 4), ParseTimestamp(reader.GetString(5)),
                ReadNullableTimestamp(reader, 6), reader.GetInt64(7))
            : null;
    }

    private static async Task<HostWorkLease?> ReadLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string owner,
        string leaseId,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = HostLeaseSelect + " WHERE owner_principal_id=$owner AND lease_id=$lease;";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$lease", leaseId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadLease(reader) : null;
    }

    private static async Task<HostWorkLease?> ReadLatestLeaseByRunAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string owner,
        string runId,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = HostLeaseSelect + " WHERE owner_principal_id=$owner AND run_id=$run ORDER BY attempt DESC, lease_id DESC LIMIT 1;";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$run", runId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadLease(reader) : null;
    }

    private static async Task<HostWorkLease?> ReadLatestLeaseForHostAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string owner,
        string hostId,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = HostLeaseSelect + " WHERE owner_principal_id=$owner AND host_id=$host ORDER BY issued_at DESC, lease_id DESC LIMIT 1;";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$host", hostId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadLease(reader) : null;
    }

    private static async Task<IReadOnlyList<HostWorkLease>> ReadActiveLeasesForHostAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string owner,
        string hostId,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = HostLeaseSelect + " WHERE owner_principal_id=$owner AND host_id=$host AND state IN ('OFFERED','ACKNOWLEDGED','RUNNING','DISCONNECTED');";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$host", hostId);
        var values = new List<HostWorkLease>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            values.Add(ReadLease(reader));
        return values;
    }

    private static async Task<IReadOnlyList<HostWorkLease>> ReadExpiredActiveLeasesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        DateTimeOffset now,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = HostLeaseSelect + " WHERE state IN ('OFFERED','ACKNOWLEDGED','RUNNING','DISCONNECTED') AND execute_until <= $now;";
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        var values = new List<HostWorkLease>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            values.Add(ReadLease(reader));
        return values;
    }

    private static async Task<IReadOnlyList<HostLeaseResource>> ReadLeaseResourcesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string owner,
        string leaseId,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT resource_id,resource_grant_version,access_mode,fingerprint
            FROM host_lease_resources
            WHERE owner_principal_id=$owner AND lease_id=$lease
            ORDER BY resource_id;
            """;
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$lease", leaseId);
        var values = new List<HostLeaseResource>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            values.Add(new(owner, leaseId, reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3)));
        }

        return values;
    }

    private static async Task<bool> HasActiveSchedulerFenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string owner,
        string runId,
        long fence,
        DateTimeOffset now,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM scheduler_leases
            WHERE owner_principal_id=$owner AND run_id=$run AND fence=$fence AND expires_at>$now;
            """;
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$fence", fence);
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not null;
    }

    private static async Task ReleaseSchedulerLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string owner,
        string runId,
        long fence,
        DateTimeOffset now,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE scheduler_leases SET expires_at=$now WHERE owner_principal_id=$owner AND run_id=$run AND fence=$fence;";
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$fence", fence);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task StartRunOnConnectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string owner,
        string runId,
        long fence,
        DateTimeOffset now,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE job_runs SET state='RUNNING',started_at=$now,fence=$fence,version=version+1
            WHERE owner_principal_id=$owner AND run_id=$run AND state='QUEUED'
              AND EXISTS(SELECT 1 FROM scheduler_leases WHERE owner_principal_id=$owner AND run_id=$run AND fence=$fence AND expires_at>$now)
              AND EXISTS(SELECT 1 FROM jobs WHERE jobs.owner_principal_id=job_runs.owner_principal_id AND jobs.job_id=job_runs.job_id AND jobs.desired_state='ACTIVE');
            """;
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        command.Parameters.AddWithValue("$fence", fence);
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$run", runId);
        if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            throw new ProductConcurrencyException("Host acknowledgement lost the scheduler fence.");
    }

    private static async Task CompleteRunOnConnectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string owner,
        string runId,
        long fence,
        string state,
        string? error,
        DateTimeOffset now,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE job_runs SET state=CASE WHEN EXISTS(SELECT 1 FROM jobs WHERE jobs.owner_principal_id=job_runs.owner_principal_id AND jobs.job_id=job_runs.job_id AND jobs.desired_state='CANCELED') THEN 'CANCELED' ELSE $state END,
                ended_at=$now,error_code=CASE WHEN EXISTS(SELECT 1 FROM jobs WHERE jobs.owner_principal_id=job_runs.owner_principal_id AND jobs.job_id=job_runs.job_id AND jobs.desired_state='CANCELED') THEN 'job_canceled' ELSE $error END,
                version=version+1
            WHERE owner_principal_id=$owner AND run_id=$run AND state='RUNNING' AND fence=$fence
              AND EXISTS(SELECT 1 FROM scheduler_leases WHERE owner_principal_id=$owner AND run_id=$run AND fence=$fence AND expires_at>$now);
            """;
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$fence", fence);
        if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            throw new ProductConcurrencyException("Host completion lost the scheduler fence.");
    }

    private static async Task UpdateRunForRequeueAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string owner,
        string runId,
        long fence,
        DateTimeOffset now,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE job_runs
            SET state='QUEUED',started_at=NULL,ended_at=NULL,error_code=NULL,version=version+1
            WHERE owner_principal_id=$owner AND run_id=$run AND state IN ('QUEUED','RUNNING','RECONCILIATION_REQUIRED') AND fence=$fence;
            """;
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$fence", fence);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task UpdateRunForReconciliationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string owner,
        string runId,
        long fence,
        DateTimeOffset now,
        string errorCode,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE job_runs
            SET state='RECONCILIATION_REQUIRED',ended_at=NULL,error_code=$error,version=version+1
            WHERE owner_principal_id=$owner AND run_id=$run AND state IN ('RUNNING','RECONCILIATION_REQUIRED') AND fence=$fence;
            """;
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$fence", fence);
        command.Parameters.AddWithValue("$error", errorCode);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task SetLeaseStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        HostWorkLease lease,
        string state,
        string? outcome,
        string? outputSha256,
        string? failureCode,
        DateTimeOffset now,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE host_work_leases
            SET state=$state,
                completed_at=CASE WHEN $terminal=1 THEN $completedAt ELSE completed_at END,
                outcome=$outcome,
                output_sha256=COALESCE($outputSha256,output_sha256),
                failure_code=$failureCode,
                version=version+1
            WHERE owner_principal_id=$owner AND lease_id=$lease AND version=$version AND state=$expectedState;
            """;
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$terminal", state is HostLeaseStates.Completed or HostLeaseStates.Failed or HostLeaseStates.ReconciliationRequired or HostLeaseStates.Declined or HostLeaseStates.Expired or HostLeaseStates.Revoked ? 1 : 0);
        command.Parameters.AddWithValue("$completedAt", FormatTimestamp(now));
        command.Parameters.AddWithValue("$outcome", (object?)outcome ?? DBNull.Value);
        command.Parameters.AddWithValue("$outputSha256", (object?)outputSha256 ?? DBNull.Value);
        command.Parameters.AddWithValue("$failureCode", (object?)failureCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$owner", lease.OwnerPrincipalId);
        command.Parameters.AddWithValue("$lease", lease.LeaseId);
        command.Parameters.AddWithValue("$version", lease.Version);
        command.Parameters.AddWithValue("$expectedState", lease.State);
        if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            throw new ProductConcurrencyException("Host lease transition lost the expected state/version.");
    }

    private static async Task<bool> HasAcceptedLeaseEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string owner,
        string leaseId,
        string eventType,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM host_lease_events WHERE owner_principal_id=$owner AND lease_id=$lease AND type=$type LIMIT 1;";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$lease", leaseId);
        command.Parameters.AddWithValue("$type", eventType);
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not null;
    }

    private static async Task<bool> ResumeLeaseRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        HostWorkLease lease,
        DateTimeOffset now,
        CancellationToken token)
    {
        if (!await HasActiveSchedulerFenceAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId, lease.SchedulerFence, now, token).ConfigureAwait(false))
            return false;

        var run = await ReadJobRunAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId, token).ConfigureAwait(false);
        if (run is null || run.Fence != lease.SchedulerFence)
            return false;
        if (run.State == "RUNNING")
            return true;
        if (run.State != "RECONCILIATION_REQUIRED")
            return false;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE job_runs
            SET state='RUNNING',ended_at=NULL,error_code=NULL,version=version+1
            WHERE owner_principal_id=$owner AND run_id=$run AND state='RECONCILIATION_REQUIRED' AND fence=$fence
              AND EXISTS(SELECT 1 FROM scheduler_leases WHERE owner_principal_id=$owner AND run_id=$run AND fence=$fence AND expires_at>$now);
            """;
        command.Parameters.AddWithValue("$owner", lease.OwnerPrincipalId);
        command.Parameters.AddWithValue("$run", lease.RunId);
        command.Parameters.AddWithValue("$fence", lease.SchedulerFence);
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
    }

    private static async Task<HostMessageBusinessResponse> MoveLeaseToReconciliationRequiredAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RemoteHost host,
        HostWorkLease lease,
        DateTimeOffset now,
        string failureCode,
        string runErrorCode,
        bool releaseSchedulerLease,
        string resolution,
        CancellationToken token)
    {
        await SetLeaseStateAsync(connection, transaction, lease, HostLeaseStates.ReconciliationRequired, HostLeaseCompletionOutcomes.Unknown, null, failureCode, now, token)
            .ConfigureAwait(false);
        await UpdateRunForReconciliationAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId, lease.SchedulerFence, now, runErrorCode, token)
            .ConfigureAwait(false);
        await UpsertJobRunBlockerAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId,
            JobRunBlockerCodes.HostDisconnected, host.HostId, null, null, failureCode, now, token)
            .ConfigureAwait(false);
        if (releaseSchedulerLease)
        {
            await ReleaseSchedulerLeaseAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId, lease.SchedulerFence, now, token)
                .ConfigureAwait(false);
            await RestoreHostLifecycleIfIdleAsync(connection, transaction, lease.OwnerPrincipalId, host.HostId, now, token)
                .ConfigureAwait(false);
        }

        var run = await ReadJobRunAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId, token).ConfigureAwait(false);
        var updatedLease = await ReadLeaseAsync(connection, transaction, lease.OwnerPrincipalId, lease.LeaseId, token).ConfigureAwait(false);
        return new(200, JsonSerializer.Serialize(new
        {
            resolution,
            lease = updatedLease is null ? null : LeaseDto(updatedLease, await ReadLeaseResourcesAsync(connection, transaction, updatedLease.OwnerPrincipalId, updatedLease.LeaseId, token).ConfigureAwait(false)),
            run = run is null ? null : new { runId = run.RunId, state = run.State, errorCode = run.ErrorCode, version = run.Version },
        }));
    }

    private static async Task<long> ReadNextLeaseEventSequenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string owner,
        string leaseId,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(sequence),0)+1 FROM host_lease_events WHERE owner_principal_id=$owner AND lease_id=$lease;";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$lease", leaseId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<(HostWorkLease? Lease, HostMessageBusinessResponse? Problem)> ValidateLeaseMutationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RemoteHost host,
        string leaseId,
        long leaseVersion,
        string localAttemptId,
        DateTimeOffset now,
        bool requireUnexpired,
        bool requireCurrentGrants,
        bool requireRunningRun,
        string[] allowedStates,
        CancellationToken token)
    {
        RemoteHostValidation.ValidateIdentifier(localAttemptId, nameof(localAttemptId));
        var lease = await ReadLeaseAsync(connection, transaction, host.OwnerPrincipalId, leaseId, token).ConfigureAwait(false);
        if (lease is null || lease.HostId != host.HostId || lease.Version != leaseVersion)
            return (null, HostLeaseProblem(409, "host_lease_invalid"));
        if (lease.LocalAttemptId != localAttemptId)
            return (null, HostLeaseProblem(409, "host_attempt_mismatch"));
        if (allowedStates.All(state => state != lease.State))
            return (null, HostLeaseProblem(409, "host_lease_invalid"));
        if (requireUnexpired && lease.ExecuteUntil <= now)
            return (null, HostLeaseProblem(409, "host_lease_expired"));
        if (requireCurrentGrants && !await LeaseGrantsAreCurrentAsync(connection, transaction, lease, token).ConfigureAwait(false))
            return (null, HostLeaseProblem(409, "host_lease_grant_changed"));
        if (requireRunningRun)
        {
            if (!await HasActiveSchedulerFenceAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId, lease.SchedulerFence, now, token).ConfigureAwait(false))
                return (null, HostLeaseProblem(409, "host_lease_invalid"));
            var run = await ReadJobRunAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId, token).ConfigureAwait(false);
            if (run is null || run.State != "RUNNING" || run.Fence != lease.SchedulerFence)
                return (null, HostLeaseProblem(409, "host_lease_invalid"));
        }

        return (lease, null);
    }

    private static List<HostLeaseEvent> ValidateLeaseEventBatch(
        HostWorkLease lease,
        IReadOnlyList<HostLeaseEvent> events,
        long nextSequence,
        DateTimeOffset now)
    {
        if (events.Count is < 1 or > RemoteEventLimit)
            throw new HostRequestValidationException(400, "host_invalid_request");

        var validated = new List<HostLeaseEvent>(events.Count);
        var seenEventIds = new HashSet<string>(StringComparer.Ordinal);
        var simulatedState = lease.State;
        var stepStartedObserved = simulatedState == HostLeaseStates.Running;
        foreach (var item in events)
        {
            RemoteHostValidation.ValidateIdentifier(item.EventId, nameof(events));
            if (!seenEventIds.Add(item.EventId) || item.Sequence != nextSequence)
                throw new HostRequestValidationException(409, "host_event_sequence_invalid");
            nextSequence++;

            if (!HostLeaseEventTypes.IsValid(item.Type))
                throw new HostRequestValidationException(400, "host_invalid_request");
            if (item.OccurredAt == default || item.OccurredAt.Offset != TimeSpan.Zero)
                throw new HostRequestValidationException(400, "host_invalid_request");
            if (item.OccurredAt < lease.IssuedAt
                || item.OccurredAt > now.AddSeconds(RemoteHostProtocol.MaximumClockSkewSeconds))
            {
                throw new HostRequestValidationException(400, "host_invalid_request");
            }

            if (item.Type == HostLeaseEventTypes.StepStarted)
            {
                if (simulatedState != HostLeaseStates.Acknowledged || stepStartedObserved)
                    throw new HostRequestValidationException(409, "host_lease_invalid");
                simulatedState = HostLeaseStates.Running;
                stepStartedObserved = true;
            }
            else if (item.Type is HostLeaseEventTypes.StepCompleted or HostLeaseEventTypes.JobCompleted or HostLeaseEventTypes.JobFailed)
            {
                if (simulatedState != HostLeaseStates.Running)
                    throw new HostRequestValidationException(409, "host_lease_invalid");
            }

            string? summary = item.Summary is null
                ? null
                : ProductContentValidation.Text(item.Summary, nameof(events), RemoteEventSummaryLimit);
            string? dataJson = null;
            if (item.DataJson is not null)
            {
                using var document = JsonDocument.Parse(item.DataJson);
                dataJson = ProductContentValidation.Json(document.RootElement, nameof(events), RemoteEventDataLimitBytes).GetRawText();
            }

            validated.Add(item with { Summary = summary, DataJson = dataJson });
        }

        return validated;
    }

    private static async Task<HostMessageBusinessResponse> GuardHostRequestAsync(
        Func<Task<HostMessageBusinessResponse>> callback)
    {
        try
        {
            return await callback().ConfigureAwait(false);
        }
        catch (HostRequestValidationException exception)
        {
            return HostLeaseProblem(exception.StatusCode, exception.Code);
        }
        catch (ArgumentException)
        {
            return HostLeaseProblem(400, "host_invalid_request");
        }
        catch (InvalidDataException)
        {
            return HostLeaseProblem(400, "host_invalid_request");
        }
        catch (JsonException)
        {
            return HostLeaseProblem(400, "host_invalid_request");
        }
        catch (ProductConcurrencyException)
        {
            return HostLeaseProblem(409, "host_concurrency_conflict");
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return HostLeaseProblem(409, "host_event_sequence_invalid");
        }
    }

    private sealed class HostRequestValidationException(int statusCode, string code) : Exception(code)
    {
        public int StatusCode { get; } = statusCode;

        public string Code { get; } = code;
    }

    private static async Task<bool> LeaseGrantsAreCurrentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        HostWorkLease lease,
        CancellationToken token)
    {
        await using (var capability = connection.CreateCommand())
        {
            capability.Transaction = transaction;
            capability.CommandText = """
                SELECT 1 FROM host_capability_grants
                WHERE owner_principal_id=$owner AND host_id=$host AND capability_id=$capabilityId
                  AND capability_version=$capabilityVersion AND version=$version AND revoked_at IS NULL;
                """;
            capability.Parameters.AddWithValue("$owner", lease.OwnerPrincipalId);
            capability.Parameters.AddWithValue("$host", lease.HostId);
            capability.Parameters.AddWithValue("$capabilityId", lease.CapabilityId);
            capability.Parameters.AddWithValue("$capabilityVersion", lease.CapabilityVersion);
            capability.Parameters.AddWithValue("$version", lease.CapabilityGrantVersion);
            if (await capability.ExecuteScalarAsync(token).ConfigureAwait(false) is null)
                return false;
        }

        var resources = await ReadLeaseResourcesAsync(connection, transaction, lease.OwnerPrincipalId, lease.LeaseId, token).ConfigureAwait(false);
        foreach (var resource in resources)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT 1 FROM host_resource_grants grant_row
                JOIN host_resources resource_row
                  ON resource_row.owner_principal_id=grant_row.owner_principal_id
                 AND resource_row.host_id=grant_row.host_id
                 AND resource_row.resource_id=grant_row.resource_id
                WHERE grant_row.owner_principal_id=$owner AND grant_row.host_id=$host AND grant_row.resource_id=$resource
                  AND grant_row.version=$version AND grant_row.revoked_at IS NULL
                  AND grant_row.access_mode=$accessMode AND resource_row.fingerprint=$fingerprint AND resource_row.state='AVAILABLE';
                """;
            command.Parameters.AddWithValue("$owner", lease.OwnerPrincipalId);
            command.Parameters.AddWithValue("$host", lease.HostId);
            command.Parameters.AddWithValue("$resource", resource.ResourceId);
            command.Parameters.AddWithValue("$version", resource.ResourceGrantVersion);
            command.Parameters.AddWithValue("$accessMode", resource.AccessMode);
            command.Parameters.AddWithValue("$fingerprint", resource.Fingerprint);
            if (await command.ExecuteScalarAsync(token).ConfigureAwait(false) is null)
                return false;
        }

        return true;
    }

    private static async Task InsertOrIgnoreHostOutputAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string owner,
        string runId,
        string output,
        bool truncated,
        DateTimeOffset now,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO job_outputs(owner_principal_id,output_ref,run_id,kind,media_type,summary,text,truncated,created_at)
            VALUES($owner,$ref,$run,'TEXT','text/plain','Remote Host output',$text,$truncated,$createdAt);
            """;
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$ref", $"output:{runId}:host");
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$text", output);
        command.Parameters.AddWithValue("$truncated", truncated ? 1 : 0);
        command.Parameters.AddWithValue("$createdAt", FormatTimestamp(now));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task SetHostLifecycleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string owner,
        string hostId,
        string lifecycle,
        DateTimeOffset now,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE remote_hosts
            SET lifecycle=$lifecycle,connection_status=$lifecycle,last_seen_at=$seenAt,version=version+1
            WHERE owner_principal_id=$owner AND host_id=$host AND lifecycle<>'REVOKED';
            """;
        command.Parameters.AddWithValue("$lifecycle", lifecycle);
        command.Parameters.AddWithValue("$seenAt", FormatTimestamp(now));
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$host", hostId);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task RestoreHostLifecycleIfIdleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string owner,
        string hostId,
        DateTimeOffset now,
        CancellationToken token)
    {
        await using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = """
            SELECT 1 FROM host_work_leases
            WHERE owner_principal_id=$owner AND host_id=$host AND state IN ('OFFERED','ACKNOWLEDGED','RUNNING','DISCONNECTED')
            LIMIT 1;
            """;
        check.Parameters.AddWithValue("$owner", owner);
        check.Parameters.AddWithValue("$host", hostId);
        if (await check.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
            return;
        await SetHostLifecycleAsync(connection, transaction, owner, hostId, RemoteHostLifecycles.Online, now, token)
            .ConfigureAwait(false);
    }

    private static string ComputeRemoteInputHash(IReadOnlyList<string> resourceIds)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { resourceIds }))));

    private static HostMessageBusinessResponse EmptyPollResponse(DateTimeOffset now)
        => new(200, JsonSerializer.Serialize(new
        {
            serverTime = now,
            nextPollAfterMs = 1000,
            lease = (object?)null,
            command = (object?)null,
        }));

    private static async Task<HostMessageBusinessResponse> PollLeaseResponseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        HostWorkLease lease,
        DateTimeOffset now,
        bool includeCommand,
        CancellationToken token)
    {
        var resources = await ReadLeaseResourcesAsync(
            connection, transaction, lease.OwnerPrincipalId, lease.LeaseId, token).ConfigureAwait(false);
        var command = includeCommand
            ? new
            {
                commandId = $"cmd:{lease.LeaseId}",
                leaseId = lease.LeaseId,
                leaseVersion = lease.Version,
                runId = lease.RunId,
                schedulerFence = lease.SchedulerFence,
                profileId = lease.ProfileId,
                capabilityId = lease.CapabilityId,
                capabilityVersion = lease.CapabilityVersion,
                capabilityGrantVersion = lease.CapabilityGrantVersion,
                resources = resources.Select(item => new
                {
                    item.ResourceId,
                    resourceGrantVersion = item.ResourceGrantVersion,
                    item.AccessMode,
                    item.Fingerprint,
                }).ToArray(),
                inputHash = lease.InputHash,
                issuedAt = lease.IssuedAt,
                executeUntil = lease.ExecuteUntil,
                input = new { resourceIds = resources.Select(item => item.ResourceId).ToArray() },
                outputLimitBytes = RemoteOutputLimitBytes,
                eventLimit = RemoteEventLimit,
            }
            : null;
        return new(200, JsonSerializer.Serialize(new
        {
            serverTime = now,
            nextPollAfterMs = includeCommand ? 250 : 1000,
            lease = LeaseDto(lease, resources),
            command,
        }));
    }

    private static HostMessageBusinessResponse HostLeaseProblem(int status, string code)
        => new(status, RemoteHostSnapshotSerializer.SerializeProblem(status, code));

    private static object LeaseDto(HostWorkLease lease, IReadOnlyList<HostLeaseResource> resources)
        => new
        {
            leaseId = lease.LeaseId,
            leaseVersion = lease.Version,
            runId = lease.RunId,
            jobId = lease.JobId,
            hostId = lease.HostId,
            schedulerFence = lease.SchedulerFence,
            attempt = lease.Attempt,
            profileId = lease.ProfileId,
            capabilityId = lease.CapabilityId,
            capabilityVersion = lease.CapabilityVersion,
            capabilityGrantVersion = lease.CapabilityGrantVersion,
            inputHash = lease.InputHash,
            state = lease.State,
            issuedAt = lease.IssuedAt,
            executeUntil = lease.ExecuteUntil,
            acknowledgedAt = lease.AcknowledgedAt,
            completedAt = lease.CompletedAt,
            localAttemptId = lease.LocalAttemptId,
            outcome = lease.Outcome,
            outputSha256 = lease.OutputSha256,
            failureCode = lease.FailureCode,
            resources = resources.Select(item => new
            {
                item.ResourceId,
                resourceGrantVersion = item.ResourceGrantVersion,
                item.AccessMode,
                item.Fingerprint,
            }).ToArray(),
        };

    private static async Task<RemoteHost?> ReadHostByIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string owner,
        string hostId,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = HostSelect + " WHERE owner_principal_id=$owner AND host_id=$host;";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$host", hostId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadHost(reader, owner) : null;
    }

    private static async Task<ProductJobRun?> ReadJobRunAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string owner,
        string runId,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT job_id,scheduled_for,state,fence,version,started_at,ended_at,model_profile_id,context_snapshot_ref,error_code FROM job_runs WHERE owner_principal_id=$owner AND run_id=$run;";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$run", runId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false)
            ? new(owner, runId, reader.GetString(0), ParseTimestamp(reader.GetString(1)), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4), ReadNullableTimestamp(reader, 5), ReadNullableTimestamp(reader, 6), ReadNullableString(reader, 7), ReadNullableString(reader, 8), ReadNullableString(reader, 9))
            : null;
    }

    private const string HostLeaseSelect = """
        SELECT owner_principal_id,lease_id,run_id,job_id,host_id,scheduler_fence,attempt,profile_id,capability_id,
               capability_version,capability_grant_version,input_hash,state,issued_at,execute_until,
               acknowledged_at,completed_at,local_attempt_id,outcome,output_sha256,failure_code,version
        FROM host_work_leases
        """;

    private static HostWorkLease ReadLease(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetInt64(10),
            reader.GetString(11),
            reader.GetString(12),
            ParseTimestamp(reader.GetString(13)),
            ParseTimestamp(reader.GetString(14)),
            ReadNullableTimestamp(reader, 15),
            ReadNullableTimestamp(reader, 16),
            ReadNullableString(reader, 17),
            ReadNullableString(reader, 18),
            ReadNullableString(reader, 19),
            ReadNullableString(reader, 20),
            reader.GetInt64(21));

    private sealed class ExecutionCapabilityJson
    {
        [JsonPropertyName("capabilityId")]
        public string CapabilityId { get; init; } = string.Empty;

        [JsonPropertyName("capabilityVersion")]
        public string CapabilityVersion { get; init; } = string.Empty;
    }
}