using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tessera.Core.Product;

namespace Tessera.Persistence.Sqlite;

public sealed record HostPairingMutation(HostPairing? Pairing, RemoteHostDetail? Host, string? Error)
{
    public bool Succeeded => Error is null;
}

public sealed record HostPairingReceiptMutation(
    HostPairing? Pairing,
    RemoteHostDetail? Host,
    ProductIdempotencyReceipt? Receipt,
    bool Replayed,
    string? Error)
{
    public bool Succeeded => Error is null;
}

public sealed record HostPairingCreateResult(
    HostPairing? Pairing,
    ProductIdempotencyReceipt? Receipt,
    bool Replayed,
    string? Error)
{
    public bool Succeeded => Error is null;
}

public sealed record RemoteHostReceiptMutation(
    RemoteHostDetail? Host,
    ProductIdempotencyReceipt? Receipt,
    bool Replayed,
    string? Error)
{
    public bool Succeeded => Error is null;
}

public sealed partial class SqliteKernelStore
{
    public async Task<HostPairingCreateResult> CreateHostPairingAsync(
        string owner, string pairingId, string claimSecretHash,
        string idempotencyKey, string requestHash,
        DateTimeOffset createdAt, DateTimeOffset expiresAt, CancellationToken token = default)
    {
        const string routeFamily = "host-pairing-create";
        RemoteHostValidation.ValidateIdentifier(pairingId, nameof(pairingId));
        RemoteHostValidation.ValidateLowerHex(claimSecretHash, 64, nameof(claimSecretHash));
        RemoteHostValidation.ValidateIdentifier(idempotencyKey, nameof(idempotencyKey));
        RemoteHostValidation.ValidateLowerHex(requestHash, 64, nameof(requestHash));
        if (expiresAt <= createdAt || expiresAt - createdAt > RemoteHostValidation.MaximumPairingTtl)
            throw new ArgumentOutOfRangeException(nameof(expiresAt));

        await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var prior = await ReadReceiptAsync(
            connection, transaction, owner, routeFamily, idempotencyKey, token).ConfigureAwait(false);
        if (prior is not null)
        {
            return string.Equals(prior.RequestHash, requestHash, StringComparison.Ordinal)
                ? new(null, prior, true, null)
                : new(null, null, false, "idempotency_conflict");
        }
        await using (var reusedSecret = connection.CreateCommand())
        {
            reusedSecret.Transaction = transaction;
            reusedSecret.CommandText = """
                SELECT 1 FROM host_pairings
                WHERE owner_principal_id=$owner AND claim_secret_hash=$hash LIMIT 1;
                """;
            reusedSecret.Parameters.AddWithValue("$owner", owner);
            reusedSecret.Parameters.AddWithValue("$hash", claimSecretHash);
            if (await reusedSecret.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
            {
                var rejection = CreateRemoteHostReceipt(owner, routeFamily, idempotencyKey,
                    requestHash, PairingStatus("pairing_consumed"),
                    RemoteHostSnapshotSerializer.SerializeProblem(
                        PairingStatus("pairing_consumed"), "pairing_consumed"),
                    "host_pairing", pairingId, createdAt);
                await CommitRemoteHostMutationAsync(
                    connection, transaction, rejection, token).ConfigureAwait(false);
                return new(null, rejection, false, "pairing_consumed");
            }
        }
        await using (var cancel = connection.CreateCommand())
        {
            cancel.Transaction = transaction;
            cancel.CommandText = """
                UPDATE host_pairings SET state='CANCELED',canceled_at=$now,version=version+1
                WHERE owner_principal_id=$owner AND state IN ('ISSUED','CLAIMED');
                """;
            cancel.Parameters.AddWithValue("$owner", owner);
            cancel.Parameters.AddWithValue("$now", FormatTimestamp(createdAt));
            await cancel.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO host_pairings(owner_principal_id,pairing_id,claim_secret_hash,state,
                    failed_claims,failed_confirmations,requested_host_json,created_at,expires_at,
                    claimed_at,confirmed_at,canceled_at,version)
                VALUES($owner,$pairing,$hash,'ISSUED',0,0,NULL,$created,$expires,NULL,NULL,NULL,1);
                """;
            insert.Parameters.AddWithValue("$owner", owner);
            insert.Parameters.AddWithValue("$pairing", pairingId);
            insert.Parameters.AddWithValue("$hash", claimSecretHash);
            insert.Parameters.AddWithValue("$created", FormatTimestamp(createdAt));
            insert.Parameters.AddWithValue("$expires", FormatTimestamp(expiresAt));
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        var pairing = new HostPairing(owner, pairingId, claimSecretHash, HostPairingStates.Issued,
            0, 0, null, createdAt, expiresAt, null, null, null, 1);
        var receipt = new ProductIdempotencyReceipt(
            owner, routeFamily, idempotencyKey, requestHash, 201,
            RemoteHostSnapshotSerializer.SerializePairing(pairing),
            "host_pairing", pairingId, createdAt);
        await CommitRemoteHostMutationAsync(connection, transaction, receipt, token).ConfigureAwait(false);
        return new(pairing, receipt, false, null);
    }

    public async Task<HostPairingReceiptMutation> ClaimHostPairingAsync(
        string pairingId, string claimSecret, HostClaim claim,
        string idempotencyKey, string requestHash, DateTimeOffset now,
        CancellationToken token = default)
    {
        const string routeFamily = "host-pairing-claim";
        RemoteHostValidation.ValidateIdentifier(pairingId, nameof(pairingId));
        RemoteHostValidation.ValidateClaim(claim);
        RemoteHostValidation.ValidateIdentifier(idempotencyKey, nameof(idempotencyKey));
        RemoteHostValidation.ValidateLowerHex(requestHash, 64, nameof(requestHash));
        requestHash = BindTargetRequestHash(pairingId, requestHash);
        await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var pairing = await ReadPairingByIdAsync(connection, transaction, pairingId, token).ConfigureAwait(false);
        if (pairing is null) return new(null, null, null, false, "pairing_not_found");
        var prior = await ReadReceiptAsync(
            connection, transaction, pairing.OwnerPrincipalId, routeFamily, idempotencyKey, token).ConfigureAwait(false);
        if (prior is not null)
        {
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return string.Equals(prior.RequestHash, requestHash, StringComparison.Ordinal)
                ? new(null, null, prior, true, null)
                : new(null, null, null, false, "idempotency_conflict");
        }
        var stateError = PairingStateError(pairing, now, forConfirmation: false);
        if (stateError is not null)
        {
            if (stateError == "pairing_expired")
                await ExpirePairingAsync(connection, transaction, pairing, now, token).ConfigureAwait(false);
            var rejection = CreateRemoteHostReceipt(pairing.OwnerPrincipalId, routeFamily,
                idempotencyKey, requestHash, PairingStatus(stateError),
                RemoteHostSnapshotSerializer.SerializeProblem(PairingStatus(stateError), stateError),
                "host_pairing", pairingId, now);
            await CommitRemoteHostMutationAsync(connection, transaction, rejection, token).ConfigureAwait(false);
            return new(null, null, rejection, false, stateError);
        }
        if (!RemoteHostValidation.ClaimSecretMatches(pairing.ClaimSecretHash, claimSecret))
        {
            var failures = pairing.FailedClaims + 1;
            var exhausted = failures >= RemoteHostValidation.MaximumClaimAttempts;
            await using var reject = connection.CreateCommand();
            reject.Transaction = transaction;
            reject.CommandText = """
                UPDATE host_pairings SET failed_claims=$failures,
                    state=CASE WHEN $exhausted=1 THEN 'CANCELED' ELSE state END,
                    canceled_at=CASE WHEN $exhausted=1 THEN $now ELSE canceled_at END,
                    version=version+1
                WHERE owner_principal_id=$owner AND pairing_id=$pairing AND version=$version;
                """;
            reject.Parameters.AddWithValue("$failures", failures);
            reject.Parameters.AddWithValue("$exhausted", exhausted);
            reject.Parameters.AddWithValue("$now", FormatTimestamp(now));
            reject.Parameters.AddWithValue("$owner", pairing.OwnerPrincipalId);
            reject.Parameters.AddWithValue("$pairing", pairing.PairingId);
            reject.Parameters.AddWithValue("$version", pairing.Version);
            await reject.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            var error = exhausted ? "pairing_attempts_exceeded" : "pairing_invalid_request";
            var rejection = CreateRemoteHostReceipt(pairing.OwnerPrincipalId, routeFamily,
                idempotencyKey, requestHash, PairingStatus(error),
                RemoteHostSnapshotSerializer.SerializeProblem(PairingStatus(error), error),
                "host_pairing", pairingId, now);
            await CommitRemoteHostMutationAsync(connection, transaction, rejection, token).ConfigureAwait(false);
            return new(null, null, rejection, false, error);
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE host_pairings SET state='CLAIMED',requested_host_json=$claim,
                    claimed_at=$now,version=version+1
                WHERE owner_principal_id=$owner AND pairing_id=$pairing AND state='ISSUED' AND version=$version;
                """;
            update.Parameters.AddWithValue("$claim", JsonSerializer.Serialize(claim));
            update.Parameters.AddWithValue("$now", FormatTimestamp(now));
            update.Parameters.AddWithValue("$owner", pairing.OwnerPrincipalId);
            update.Parameters.AddWithValue("$pairing", pairing.PairingId);
            update.Parameters.AddWithValue("$version", pairing.Version);
            if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                return await RejectPairingAsync(connection, transaction, pairing.OwnerPrincipalId,
                    routeFamily, idempotencyKey, requestHash, "pairing_consumed", pairingId,
                    now, token).ConfigureAwait(false);
        }
        var claimed = pairing with
        {
            State = HostPairingStates.Claimed,
            RequestedHost = claim,
            ClaimedAt = now,
            Version = pairing.Version + 1,
        };
        var receipt = CreateRemoteHostReceipt(pairing.OwnerPrincipalId, routeFamily,
            idempotencyKey, requestHash, 202, RemoteHostSnapshotSerializer.SerializePairing(claimed),
            "host_pairing", pairingId, now);
        await CommitRemoteHostMutationAsync(connection, transaction, receipt, token).ConfigureAwait(false);
        return new(claimed, null, receipt, false, null);
    }

    private static int PairingStatus(string error) => error switch
    {
        "pairing_not_found" => 404,
        "pairing_invalid_request" => 400,
        "pairing_expired" or "pairing_canceled" or "pairing_consumed" or
        "pairing_attempts_exceeded" or "pairing_confirmation_mismatch" or
        "pairing_grant_not_requested" or "pairing_version_conflict" or
        "idempotency_conflict" => 409,
        _ => 400,
    };

    private static int HostStatus(string error) => error switch
    {
        "host_not_found" => 404,
        "host_version_conflict" or "host_revoked" or "host_grant_not_advertised" or
        "idempotency_conflict" => 409,
        _ => 400,
    };

    private async Task<HostPairingReceiptMutation> RejectPairingAsync(
        SqliteConnection connection, SqliteTransaction transaction, string owner,
        string routeFamily, string idempotencyKey, string requestHash, string error,
        string pairingId, DateTimeOffset now, CancellationToken token)
    {
        var status = PairingStatus(error);
        var receipt = CreateRemoteHostReceipt(owner, routeFamily, idempotencyKey, requestHash,
            status, RemoteHostSnapshotSerializer.SerializeProblem(status, error),
            "host_pairing", pairingId, now);
        await CommitRemoteHostMutationAsync(connection, transaction, receipt, token).ConfigureAwait(false);
        return new(null, null, receipt, false, error);
    }

    private async Task<RemoteHostReceiptMutation> RejectHostAsync(
        SqliteConnection connection, SqliteTransaction transaction, string owner,
        string routeFamily, string idempotencyKey, string requestHash, string error,
        string hostId, DateTimeOffset now, CancellationToken token)
    {
        var status = HostStatus(error);
        var receipt = CreateRemoteHostReceipt(owner, routeFamily, idempotencyKey, requestHash,
            status, RemoteHostSnapshotSerializer.SerializeProblem(status, error),
            "remote_host", hostId, now);
        await CommitRemoteHostMutationAsync(connection, transaction, receipt, token).ConfigureAwait(false);
        return new(null, receipt, false, error);
    }

    private static ProductIdempotencyReceipt CreateRemoteHostReceipt(
        string owner, string routeFamily, string idempotencyKey, string requestHash,
        int status, string body, string resourceType, string resourceId, DateTimeOffset createdAt)
        => new(owner, routeFamily, idempotencyKey, requestHash, status, body,
            resourceType, resourceId, createdAt);

    private static string BindTargetRequestHash(string targetId, string requestHash)
        => Convert.ToHexStringLower(SHA256.HashData(
            Encoding.ASCII.GetBytes($"{targetId}\n{requestHash}")));

    private async Task CommitRemoteHostMutationAsync(
        SqliteConnection connection, SqliteTransaction transaction,
        ProductIdempotencyReceipt receipt, CancellationToken token)
    {
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO idempotency_receipts(owner_principal_id,route_family,idempotency_key,
                    request_hash,response_status,response_body_json,resource_type,resource_id,created_at)
                VALUES($owner,$route,$key,$hash,$status,$body,$resourceType,$resourceId,$created);
                """;
            command.Parameters.AddWithValue("$owner", receipt.OwnerPrincipalId);
            command.Parameters.AddWithValue("$route", receipt.RouteFamily);
            command.Parameters.AddWithValue("$key", receipt.IdempotencyKey);
            command.Parameters.AddWithValue("$hash", receipt.RequestHash);
            command.Parameters.AddWithValue("$status", receipt.ResponseStatus);
            command.Parameters.AddWithValue("$body", receipt.ResponseBodyJson);
            command.Parameters.AddWithValue("$resourceType", receipt.ResourceType);
            command.Parameters.AddWithValue("$resourceId", receipt.ResourceId);
            command.Parameters.AddWithValue("$created", FormatTimestamp(receipt.CreatedAt));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        if (RemoteHostBeforeCommitAsync is not null)
            await RemoteHostBeforeCommitAsync(token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    public async Task<HostPairingReceiptMutation> ConfirmHostPairingAsync(
        string owner, string pairingId, long expectedVersion, string confirmationCode,
        string hostId, string displayName,
        IReadOnlyList<HostCapabilityGrantRequest> capabilityGrants,
        IReadOnlyList<HostResourceGrantRequest> resourceGrants,
        string idempotencyKey, string requestHash, DateTimeOffset now,
        CancellationToken token = default)
    {
        const string routeFamily = "host-pairing-confirm";
        RemoteHostValidation.ValidateIdentifier(pairingId, nameof(pairingId));
        RemoteHostValidation.ValidateIdentifier(hostId, nameof(hostId));
        RemoteHostValidation.ValidatePrintableText(displayName, nameof(displayName));
        ValidateGrantRequests(capabilityGrants, resourceGrants);
        RemoteHostValidation.ValidateIdentifier(idempotencyKey, nameof(idempotencyKey));
        RemoteHostValidation.ValidateLowerHex(requestHash, 64, nameof(requestHash));
        requestHash = BindTargetRequestHash(pairingId, requestHash);
        await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var prior = await ReadReceiptAsync(
            connection, transaction, owner, routeFamily, idempotencyKey, token).ConfigureAwait(false);
        if (prior is not null)
        {
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return string.Equals(prior.RequestHash, requestHash, StringComparison.Ordinal)
                ? new(null, null, prior, true, null)
                : new(null, null, null, false, "idempotency_conflict");
        }
        var pairing = await ReadPairingAsync(connection, transaction, owner, pairingId, token).ConfigureAwait(false);
        if (pairing is null)
            return await RejectPairingAsync(connection, transaction, owner, routeFamily, idempotencyKey,
                requestHash, "pairing_not_found", pairingId, now, token).ConfigureAwait(false);
        var stateError = PairingStateError(pairing, now, forConfirmation: true);
        if (stateError is not null)
        {
            if (stateError == "pairing_expired")
                await ExpirePairingAsync(connection, transaction, pairing, now, token).ConfigureAwait(false);
            return await RejectPairingAsync(connection, transaction, owner, routeFamily, idempotencyKey,
                requestHash, stateError, pairingId, now, token).ConfigureAwait(false);
        }
        if (pairing.Version != expectedVersion)
            return await RejectPairingAsync(connection, transaction, owner, routeFamily, idempotencyKey,
                requestHash, "pairing_version_conflict", pairingId, now, token).ConfigureAwait(false);
        var claim = pairing.RequestedHost!;
        var expectedCode = RemoteHostValidation.DeriveConfirmationCode(pairing.PairingId, claim.PublicKey);
        if (!FixedAsciiEquals(expectedCode, confirmationCode))
        {
            var failures = pairing.FailedConfirmations + 1;
            var exhausted = failures >= RemoteHostValidation.MaximumConfirmationAttempts;
            await using var reject = connection.CreateCommand();
            reject.Transaction = transaction;
            reject.CommandText = """
                UPDATE host_pairings SET failed_confirmations=$failures,
                    state=CASE WHEN $exhausted=1 THEN 'CANCELED' ELSE state END,
                    canceled_at=CASE WHEN $exhausted=1 THEN $now ELSE canceled_at END,
                    version=version+1
                WHERE owner_principal_id=$owner AND pairing_id=$pairing AND version=$version;
                """;
            reject.Parameters.AddWithValue("$failures", failures);
            reject.Parameters.AddWithValue("$exhausted", exhausted);
            reject.Parameters.AddWithValue("$now", FormatTimestamp(now));
            reject.Parameters.AddWithValue("$owner", owner);
            reject.Parameters.AddWithValue("$pairing", pairingId);
            reject.Parameters.AddWithValue("$version", pairing.Version);
            await reject.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            var error = exhausted ? "pairing_attempts_exceeded" : "pairing_confirmation_mismatch";
            return await RejectPairingAsync(connection, transaction, owner, routeFamily, idempotencyKey,
                requestHash, error, pairingId, now, token).ConfigureAwait(false);
        }
        if (!GrantsAreRequested(claim, capabilityGrants, resourceGrants))
            return await RejectPairingAsync(connection, transaction, owner, routeFamily, idempotencyKey,
                requestHash, "pairing_grant_not_requested", pairingId, now, token).ConfigureAwait(false);

        await using (var existingIdentity = connection.CreateCommand())
        {
            existingIdentity.Transaction = transaction;
            existingIdentity.CommandText = """
                SELECT 1 FROM remote_hosts
                WHERE owner_principal_id=$owner AND public_key_jwk=$jwk LIMIT 1;
                """;
            existingIdentity.Parameters.AddWithValue("$owner", owner);
            existingIdentity.Parameters.AddWithValue("$jwk", claim.PublicKey.CanonicalJson);
            if (await existingIdentity.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                return await RejectPairingAsync(connection, transaction, owner, routeFamily,
                    idempotencyKey, requestHash, "pairing_consumed", pairingId, now, token)
                    .ConfigureAwait(false);
        }

        var host = new RemoteHost(owner, hostId, displayName, claim.Platform, claim.Architecture,
            RemoteHostLifecycles.Offline, "OFFLINE", claim.PublicKey, 1, claim.Protection,
            claim.AgentVersion, claim.ProtocolVersion, 1, 0, null, now, null, 1);
        await InsertHostAsync(connection, transaction, host, claim, capabilityGrants, resourceGrants, now, token)
            .ConfigureAwait(false);
        await using (var confirm = connection.CreateCommand())
        {
            confirm.Transaction = transaction;
            confirm.CommandText = """
                UPDATE host_pairings SET state='CONFIRMED',confirmed_at=$now,version=version+1
                WHERE owner_principal_id=$owner AND pairing_id=$pairing AND state='CLAIMED' AND version=$version;
                """;
            confirm.Parameters.AddWithValue("$now", FormatTimestamp(now));
            confirm.Parameters.AddWithValue("$owner", owner);
            confirm.Parameters.AddWithValue("$pairing", pairingId);
            confirm.Parameters.AddWithValue("$version", pairing.Version);
            if (await confirm.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                return await RejectPairingAsync(connection, transaction, owner, routeFamily,
                    idempotencyKey, requestHash, "pairing_version_conflict", pairingId,
                    now, token).ConfigureAwait(false);
        }
        var confirmed = pairing with
        {
            State = HostPairingStates.Confirmed,
            ConfirmedAt = now,
            Version = pairing.Version + 1,
        };
        var detail = await ReadHostDetailAsync(connection, transaction, owner, hostId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Confirmed Host snapshot is missing.");
        var receipt = CreateRemoteHostReceipt(owner, routeFamily, idempotencyKey, requestHash, 201,
            RemoteHostSnapshotSerializer.SerializeHost(detail), "remote_host", hostId, now);
        await CommitRemoteHostMutationAsync(connection, transaction, receipt, token).ConfigureAwait(false);
        return new(confirmed, detail, receipt, false, null);
    }

    public async Task<HostPairingReceiptMutation> CancelHostPairingAsync(
        string owner, string pairingId, long expectedVersion,
        string idempotencyKey, string requestHash, DateTimeOffset now,
        CancellationToken token = default)
    {
        const string routeFamily = "host-pairing-cancel";
        RemoteHostValidation.ValidateIdentifier(pairingId, nameof(pairingId));
        RemoteHostValidation.ValidateIdentifier(idempotencyKey, nameof(idempotencyKey));
        RemoteHostValidation.ValidateLowerHex(requestHash, 64, nameof(requestHash));
        requestHash = BindTargetRequestHash(pairingId, requestHash);
        await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var prior = await ReadReceiptAsync(
            connection, transaction, owner, routeFamily, idempotencyKey, token).ConfigureAwait(false);
        if (prior is not null)
        {
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return string.Equals(prior.RequestHash, requestHash, StringComparison.Ordinal)
                ? new(null, null, prior, true, null)
                : new(null, null, null, false, "idempotency_conflict");
        }
        var pairing = await ReadPairingAsync(connection, transaction, owner, pairingId, token).ConfigureAwait(false);
        if (pairing is null)
            return await RejectPairingAsync(connection, transaction, owner, routeFamily, idempotencyKey,
                requestHash, "pairing_not_found", pairingId, now, token).ConfigureAwait(false);
        if (pairing.State == HostPairingStates.Canceled)
            return await RejectPairingAsync(connection, transaction, owner, routeFamily, idempotencyKey,
                requestHash, "pairing_canceled", pairingId, now, token).ConfigureAwait(false);
        if (pairing.State is HostPairingStates.Confirmed)
            return await RejectPairingAsync(connection, transaction, owner, routeFamily, idempotencyKey,
                requestHash, "pairing_consumed", pairingId, now, token).ConfigureAwait(false);
        if (pairing.State == HostPairingStates.Expired || pairing.ExpiresAt <= now)
        {
            await ExpirePairingAsync(connection, transaction, pairing, now, token).ConfigureAwait(false);
            return await RejectPairingAsync(connection, transaction, owner, routeFamily, idempotencyKey,
                requestHash, "pairing_expired", pairingId, now, token).ConfigureAwait(false);
        }
        if (pairing.State is not (HostPairingStates.Issued or HostPairingStates.Claimed))
            return await RejectPairingAsync(connection, transaction, owner, routeFamily, idempotencyKey,
                requestHash, "pairing_invalid_request", pairingId, now, token).ConfigureAwait(false);
        if (pairing.Version != expectedVersion)
            return await RejectPairingAsync(connection, transaction, owner, routeFamily, idempotencyKey,
                requestHash, "pairing_version_conflict", pairingId, now, token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE host_pairings SET state='CANCELED',canceled_at=$now,version=version+1
            WHERE owner_principal_id=$owner AND pairing_id=$pairing
              AND state IN ('ISSUED','CLAIMED') AND version=$version;
            """;
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$pairing", pairingId);
        command.Parameters.AddWithValue("$version", expectedVersion);
        if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            return await RejectPairingAsync(connection, transaction, owner, routeFamily,
                idempotencyKey, requestHash, "pairing_consumed", pairingId, now, token)
                .ConfigureAwait(false);
        var canceled = pairing with
        {
            State = HostPairingStates.Canceled,
            CanceledAt = now,
            Version = expectedVersion + 1,
        };
        var receipt = CreateRemoteHostReceipt(owner, routeFamily, idempotencyKey, requestHash, 200,
            RemoteHostSnapshotSerializer.SerializeVersion(canceled.Version), "host_pairing", pairingId, now);
        await CommitRemoteHostMutationAsync(connection, transaction, receipt, token).ConfigureAwait(false);
        return new(canceled, null, receipt, false, null);
    }

    public async Task<HostPairing?> GetHostPairingAsync(
        string owner, string pairingId, CancellationToken token = default)
    {
        await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
        return await ReadPairingAsync(connection, null, owner, pairingId, token).ConfigureAwait(false);
    }

    public async Task<string?> ResolveHostPairingOwnerAsync(
        string pairingId, CancellationToken token = default)
    {
        await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT owner_principal_id FROM host_pairings WHERE pairing_id=$pairing;";
        command.Parameters.AddWithValue("$pairing", pairingId);
        return (string?)await command.ExecuteScalarAsync(token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RemoteHost>> ListRemoteHostsAsync(
        string owner, CancellationToken token = default)
    {
        await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = HostSelect + " WHERE owner_principal_id=$owner ORDER BY paired_at,host_id;";
        command.Parameters.AddWithValue("$owner", owner);
        var values = new List<RemoteHost>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false)) values.Add(ReadHost(reader, owner));
        return values.AsReadOnly();
    }

    public async Task<RemoteHostDetail?> GetRemoteHostDetailAsync(
        string owner, string hostId, CancellationToken token = default)
    {
        await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
        return await ReadHostDetailAsync(connection, null, owner, hostId, token).ConfigureAwait(false);
    }

    public async Task<RemoteHostReceiptMutation> UpdateRemoteHostGrantsAsync(
        string owner, string hostId, long expectedVersion,
        IReadOnlyList<HostCapabilityGrantRequest> capabilityGrants,
        IReadOnlyList<HostResourceGrantRequest> resourceGrants,
        string idempotencyKey, string requestHash, DateTimeOffset now,
        CancellationToken token = default)
    {
        const string routeFamily = "host-grants";
        RemoteHostValidation.ValidateIdentifier(hostId, nameof(hostId));
        ValidateGrantRequests(capabilityGrants, resourceGrants);
        RemoteHostValidation.ValidateIdentifier(idempotencyKey, nameof(idempotencyKey));
        RemoteHostValidation.ValidateLowerHex(requestHash, 64, nameof(requestHash));
        requestHash = BindTargetRequestHash(hostId, requestHash);
        await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var prior = await ReadReceiptAsync(
            connection, transaction, owner, routeFamily, idempotencyKey, token).ConfigureAwait(false);
        if (prior is not null)
        {
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return string.Equals(prior.RequestHash, requestHash, StringComparison.Ordinal)
                ? new(null, prior, true, null)
                : new(null, null, false, "idempotency_conflict");
        }
        var current = await ReadHostDetailAsync(connection, transaction, owner, hostId, token).ConfigureAwait(false);
        if (current is null)
            return await RejectHostAsync(connection, transaction, owner, routeFamily, idempotencyKey,
                requestHash, "host_not_found", hostId, now, token).ConfigureAwait(false);
        if (current.Host.Lifecycle == RemoteHostLifecycles.Revoked)
            return await RejectHostAsync(connection, transaction, owner, routeFamily, idempotencyKey,
                requestHash, "host_revoked", hostId, now, token).ConfigureAwait(false);
        if (current.Host.Version != expectedVersion)
            return await RejectHostAsync(connection, transaction, owner, routeFamily, idempotencyKey,
                requestHash, "host_version_conflict", hostId, now, token).ConfigureAwait(false);
        if (!GrantsAreAdvertised(current, capabilityGrants, resourceGrants))
            return await RejectHostAsync(connection, transaction, owner, routeFamily, idempotencyKey,
                requestHash, "host_grant_not_advertised", hostId, now, token).ConfigureAwait(false);
        await ReplaceGrantsAsync(connection, transaction, owner, hostId, capabilityGrants, resourceGrants, now, token)
            .ConfigureAwait(false);
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE remote_hosts SET version=version+1
                WHERE owner_principal_id=$owner AND host_id=$host AND version=$version AND lifecycle<>'REVOKED';
                """;
            update.Parameters.AddWithValue("$owner", owner);
            update.Parameters.AddWithValue("$host", hostId);
            update.Parameters.AddWithValue("$version", expectedVersion);
            if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                return await RejectHostAsync(connection, transaction, owner, routeFamily,
                    idempotencyKey, requestHash, "host_version_conflict", hostId, now, token)
                    .ConfigureAwait(false);
        }
        var detail = await ReadHostDetailAsync(connection, transaction, owner, hostId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Updated Host snapshot is missing.");
        var receipt = CreateRemoteHostReceipt(owner, routeFamily, idempotencyKey, requestHash, 200,
            RemoteHostSnapshotSerializer.SerializeHost(detail), "remote_host", hostId, now);
        await CommitRemoteHostMutationAsync(connection, transaction, receipt, token).ConfigureAwait(false);
        return new(detail, receipt, false, null);
    }

    public async Task<RemoteHostReceiptMutation> RevokeRemoteHostAsync(
        string owner, string hostId, long expectedVersion,
        string idempotencyKey, string requestHash, DateTimeOffset now,
        CancellationToken token = default)
    {
        const string routeFamily = "host-revoke";
        RemoteHostValidation.ValidateIdentifier(hostId, nameof(hostId));
        RemoteHostValidation.ValidateIdentifier(idempotencyKey, nameof(idempotencyKey));
        RemoteHostValidation.ValidateLowerHex(requestHash, 64, nameof(requestHash));
        requestHash = BindTargetRequestHash(hostId, requestHash);
        await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var prior = await ReadReceiptAsync(
            connection, transaction, owner, routeFamily, idempotencyKey, token).ConfigureAwait(false);
        if (prior is not null)
        {
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return string.Equals(prior.RequestHash, requestHash, StringComparison.Ordinal)
                ? new(null, prior, true, null)
                : new(null, null, false, "idempotency_conflict");
        }
        var current = await ReadHostDetailAsync(connection, transaction, owner, hostId, token).ConfigureAwait(false);
        if (current is null)
            return await RejectHostAsync(connection, transaction, owner, routeFamily, idempotencyKey,
                requestHash, "host_not_found", hostId, now, token).ConfigureAwait(false);
        if (current.Host.Lifecycle == RemoteHostLifecycles.Revoked)
            return await RejectHostAsync(connection, transaction, owner, routeFamily, idempotencyKey,
                requestHash, "host_revoked", hostId, now, token).ConfigureAwait(false);
        if (current.Host.Version != expectedVersion)
            return await RejectHostAsync(connection, transaction, owner, routeFamily, idempotencyKey,
                requestHash, "host_version_conflict", hostId, now, token).ConfigureAwait(false);
        await using (var host = connection.CreateCommand())
        {
            host.Transaction = transaction;
            host.CommandText = """
                UPDATE remote_hosts SET lifecycle='REVOKED',connection_status='REVOKED',
                    revoked_at=$now,version=version+1
                WHERE owner_principal_id=$owner AND host_id=$host AND version=$version AND lifecycle<>'REVOKED';
                """;
            host.Parameters.AddWithValue("$now", FormatTimestamp(now));
            host.Parameters.AddWithValue("$owner", owner);
            host.Parameters.AddWithValue("$host", hostId);
            host.Parameters.AddWithValue("$version", expectedVersion);
            if (await host.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                return await RejectHostAsync(connection, transaction, owner, routeFamily,
                    idempotencyKey, requestHash, "host_version_conflict", hostId, now, token)
                    .ConfigureAwait(false);
        }
        foreach (var table in new[] { "host_capability_grants", "host_resource_grants" })
        {
            await using var grants = connection.CreateCommand();
            grants.Transaction = transaction;
            grants.CommandText = $"UPDATE {table} SET revoked_at=$now WHERE owner_principal_id=$owner AND host_id=$host AND revoked_at IS NULL;";
            grants.Parameters.AddWithValue("$now", FormatTimestamp(now));
            grants.Parameters.AddWithValue("$owner", owner);
            grants.Parameters.AddWithValue("$host", hostId);
            await grants.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        var detail = await ReadHostDetailAsync(connection, transaction, owner, hostId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Revoked Host snapshot is missing.");
        var receipt = CreateRemoteHostReceipt(owner, routeFamily, idempotencyKey, requestHash, 202,
            RemoteHostSnapshotSerializer.SerializeHost(detail), "remote_host", hostId, now);
        await CommitRemoteHostMutationAsync(connection, transaction, receipt, token).ConfigureAwait(false);
        return new(detail, receipt, false, null);
    }

    private const string HostSelect = """
        SELECT host_id,display_name,platform,architecture,lifecycle,connection_status,
               public_key_jwk,key_version,protection,agent_version,protocol_version,
               capability_catalog_version,last_accepted_sequence,last_seen_at,paired_at,revoked_at,version
        FROM remote_hosts
        """;

    private static async Task<HostPairing?> ReadPairingByIdAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string pairingId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = PairingSelect + " WHERE pairing_id=$pairing;";
        command.Parameters.AddWithValue("$pairing", pairingId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadPairing(reader) : null;
    }

    private static async Task<HostPairing?> ReadPairingAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string owner, string pairingId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = PairingSelect + " WHERE owner_principal_id=$owner AND pairing_id=$pairing;";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$pairing", pairingId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadPairing(reader) : null;
    }

    private const string PairingSelect = """
        SELECT owner_principal_id,pairing_id,claim_secret_hash,state,failed_claims,
               failed_confirmations,requested_host_json,created_at,expires_at,claimed_at,
               confirmed_at,canceled_at,version
        FROM host_pairings
        """;

    private static HostPairing ReadPairing(SqliteDataReader reader)
        => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetInt32(4), reader.GetInt32(5), reader.IsDBNull(6) ? null : JsonSerializer.Deserialize<HostClaim>(reader.GetString(6)),
            ParseTimestamp(reader.GetString(7)), ParseTimestamp(reader.GetString(8)), ReadNullableTimestamp(reader, 9),
            ReadNullableTimestamp(reader, 10), ReadNullableTimestamp(reader, 11), reader.GetInt64(12));

    private static string? PairingStateError(HostPairing pairing, DateTimeOffset now, bool forConfirmation)
    {
        var terminal = pairing.State switch
        {
            HostPairingStates.Canceled => "pairing_canceled",
            HostPairingStates.Expired => "pairing_expired",
            HostPairingStates.Confirmed => "pairing_consumed",
            _ => null,
        };
        if (terminal is not null) return terminal;
        if (pairing.ExpiresAt <= now) return "pairing_expired";
        return pairing.State switch
        {
            HostPairingStates.Issued when !forConfirmation => null,
            HostPairingStates.Claimed when forConfirmation => null,
            HostPairingStates.Claimed => "pairing_consumed",
            _ => "pairing_invalid_request",
        };
    }

    private static async Task ExpirePairingAsync(
        SqliteConnection connection, SqliteTransaction transaction, HostPairing pairing,
        DateTimeOffset now, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE host_pairings SET state='EXPIRED',version=version+1
            WHERE owner_principal_id=$owner AND pairing_id=$pairing
              AND state IN ('ISSUED','CLAIMED') AND version=$version;
            """;
        command.Parameters.AddWithValue("$owner", pairing.OwnerPrincipalId);
        command.Parameters.AddWithValue("$pairing", pairing.PairingId);
        command.Parameters.AddWithValue("$version", pairing.Version);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task InsertHostAsync(
        SqliteConnection connection, SqliteTransaction transaction, RemoteHost host, HostClaim claim,
        IReadOnlyList<HostCapabilityGrantRequest> capabilities,
        IReadOnlyList<HostResourceGrantRequest> resources, DateTimeOffset now, CancellationToken token)
    {
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO remote_hosts(owner_principal_id,host_id,display_name,platform,architecture,
                    lifecycle,connection_status,public_key_jwk,key_version,protection,agent_version,
                    protocol_version,capability_catalog_version,last_accepted_sequence,last_seen_at,
                    paired_at,revoked_at,version)
                VALUES($owner,$host,$display,$platform,$architecture,'OFFLINE','OFFLINE',$jwk,1,
                    $protection,$agent,$protocol,1,0,NULL,$paired,NULL,1);
                """;
            command.Parameters.AddWithValue("$owner", host.OwnerPrincipalId);
            command.Parameters.AddWithValue("$host", host.HostId);
            command.Parameters.AddWithValue("$display", host.DisplayName);
            command.Parameters.AddWithValue("$platform", host.Platform);
            command.Parameters.AddWithValue("$architecture", host.Architecture);
            command.Parameters.AddWithValue("$jwk", host.PublicKey.CanonicalJson);
            command.Parameters.AddWithValue("$protection", host.Protection);
            command.Parameters.AddWithValue("$agent", host.AgentVersion);
            command.Parameters.AddWithValue("$protocol", host.ProtocolVersion);
            command.Parameters.AddWithValue("$paired", FormatTimestamp(now));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        foreach (var capability in claim.RequestedCapabilities)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO host_capability_advertisements(owner_principal_id,host_id,capability_id,
                    capability_version,schema_hash,side_effect_class,advertised_at)
                VALUES($owner,$host,$id,$version,$hash,$effect,$now);
                """;
            command.Parameters.AddWithValue("$owner", host.OwnerPrincipalId);
            command.Parameters.AddWithValue("$host", host.HostId);
            command.Parameters.AddWithValue("$id", capability.CapabilityId);
            command.Parameters.AddWithValue("$version", capability.CapabilityVersion);
            command.Parameters.AddWithValue("$hash", capability.SchemaHash);
            command.Parameters.AddWithValue("$effect", capability.SideEffectClass);
            command.Parameters.AddWithValue("$now", FormatTimestamp(now));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        foreach (var resource in claim.RequestedResources)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO host_resources(owner_principal_id,host_id,resource_id,type,display_name,
                    fingerprint,state,advertised_at,version)
                VALUES($owner,$host,$id,$type,$display,$fingerprint,$state,$now,1);
                """;
            command.Parameters.AddWithValue("$owner", host.OwnerPrincipalId);
            command.Parameters.AddWithValue("$host", host.HostId);
            command.Parameters.AddWithValue("$id", resource.ResourceId);
            command.Parameters.AddWithValue("$type", resource.Type);
            command.Parameters.AddWithValue("$display", resource.DisplayName);
            command.Parameters.AddWithValue("$fingerprint", resource.Fingerprint);
            command.Parameters.AddWithValue("$state", resource.State);
            command.Parameters.AddWithValue("$now", FormatTimestamp(now));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        await ReplaceGrantsAsync(connection, transaction, host.OwnerPrincipalId, host.HostId, capabilities, resources, now, token)
            .ConfigureAwait(false);
    }

    private static async Task ReplaceGrantsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string owner, string hostId,
        IReadOnlyList<HostCapabilityGrantRequest> capabilities,
        IReadOnlyList<HostResourceGrantRequest> resources, DateTimeOffset now, CancellationToken token)
    {
        await RevokeMissingGrantsAsync(connection, transaction, "host_capability_grants", owner, hostId,
            capabilities.Select(item => $"{item.CapabilityId}\n{item.CapabilityVersion}").ToHashSet(StringComparer.Ordinal), now, token).ConfigureAwait(false);
        await RevokeMissingGrantsAsync(connection, transaction, "host_resource_grants", owner, hostId,
            resources.Select(item => item.ResourceId).ToHashSet(StringComparer.Ordinal), now, token).ConfigureAwait(false);
        foreach (var capability in capabilities)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO host_capability_grants(owner_principal_id,host_id,capability_id,
                    capability_version,granted_at,revoked_at,version)
                SELECT $owner,$host,$id,$capabilityVersion,$now,NULL,
                    COALESCE((SELECT MAX(version) FROM host_capability_grants
                        WHERE owner_principal_id=$owner AND host_id=$host
                          AND capability_id=$id AND capability_version=$capabilityVersion),0)+1
                WHERE NOT EXISTS (
                    SELECT 1 FROM host_capability_grants
                    WHERE owner_principal_id=$owner AND host_id=$host
                      AND capability_id=$id AND capability_version=$capabilityVersion
                      AND revoked_at IS NULL);
                """;
            command.Parameters.AddWithValue("$owner", owner);
            command.Parameters.AddWithValue("$host", hostId);
            command.Parameters.AddWithValue("$id", capability.CapabilityId);
            command.Parameters.AddWithValue("$capabilityVersion", capability.CapabilityVersion);
            command.Parameters.AddWithValue("$now", FormatTimestamp(now));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        foreach (var resource in resources)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO host_resource_grants(owner_principal_id,host_id,resource_id,access_mode,
                    granted_at,revoked_at,version)
                SELECT $owner,$host,$id,$access,$now,NULL,
                    COALESCE((SELECT MAX(version) FROM host_resource_grants
                        WHERE owner_principal_id=$owner AND host_id=$host AND resource_id=$id),0)+1
                WHERE NOT EXISTS (
                    SELECT 1 FROM host_resource_grants
                    WHERE owner_principal_id=$owner AND host_id=$host
                      AND resource_id=$id AND revoked_at IS NULL);
                """;
            command.Parameters.AddWithValue("$owner", owner);
            command.Parameters.AddWithValue("$host", hostId);
            command.Parameters.AddWithValue("$id", resource.ResourceId);
            command.Parameters.AddWithValue("$access", resource.AccessMode);
            command.Parameters.AddWithValue("$now", FormatTimestamp(now));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }

    private static async Task RevokeMissingGrantsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string table, string owner,
        string hostId, HashSet<string> retained, DateTimeOffset now, CancellationToken token)
    {
        var capabilityTable = table == "host_capability_grants";
        await using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = capabilityTable
            ? $"SELECT capability_id,capability_version FROM {table} WHERE owner_principal_id=$owner AND host_id=$host AND revoked_at IS NULL;"
            : $"SELECT resource_id FROM {table} WHERE owner_principal_id=$owner AND host_id=$host AND revoked_at IS NULL;";
        read.Parameters.AddWithValue("$owner", owner);
        read.Parameters.AddWithValue("$host", hostId);
        var missing = new List<string[]>();
        await using (var reader = await read.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var key = capabilityTable ? $"{reader.GetString(0)}\n{reader.GetString(1)}" : reader.GetString(0);
                if (!retained.Contains(key))
                    missing.Add(capabilityTable ? [reader.GetString(0), reader.GetString(1)] : [reader.GetString(0)]);
            }
        }
        foreach (var key in missing)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = capabilityTable
                ? $"UPDATE {table} SET revoked_at=$now WHERE owner_principal_id=$owner AND host_id=$host AND capability_id=$id AND capability_version=$grantVersion AND revoked_at IS NULL;"
                : $"UPDATE {table} SET revoked_at=$now WHERE owner_principal_id=$owner AND host_id=$host AND resource_id=$id AND revoked_at IS NULL;";
            update.Parameters.AddWithValue("$now", FormatTimestamp(now));
            update.Parameters.AddWithValue("$owner", owner);
            update.Parameters.AddWithValue("$host", hostId);
            update.Parameters.AddWithValue("$id", key[0]);
            if (capabilityTable) update.Parameters.AddWithValue("$grantVersion", key[1]);
            await update.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }

    private static async Task<RemoteHostDetail?> ReadHostDetailAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string owner, string hostId, CancellationToken token)
    {
        RemoteHost? host;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = HostSelect + " WHERE owner_principal_id=$owner AND host_id=$host;";
            command.Parameters.AddWithValue("$owner", owner);
            command.Parameters.AddWithValue("$host", hostId);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            host = await reader.ReadAsync(token).ConfigureAwait(false) ? ReadHost(reader, owner) : null;
        }
        if (host is null) return null;
        var capabilities = new List<HostCapabilityAdvertisement>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT capability_id,capability_version,schema_hash,side_effect_class,advertised_at FROM host_capability_advertisements WHERE owner_principal_id=$owner AND host_id=$host ORDER BY capability_id,capability_version;";
            command.Parameters.AddWithValue("$owner", owner);
            command.Parameters.AddWithValue("$host", hostId);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) capabilities.Add(new(owner, hostId, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), ParseTimestamp(reader.GetString(4))));
        }
        var capabilityGrants = new List<HostCapabilityGrant>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT capability_id,capability_version,granted_at,revoked_at,version FROM host_capability_grants WHERE owner_principal_id=$owner AND host_id=$host ORDER BY capability_id,capability_version,version;";
            command.Parameters.AddWithValue("$owner", owner);
            command.Parameters.AddWithValue("$host", hostId);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) capabilityGrants.Add(new(owner, hostId, reader.GetString(0), reader.GetString(1), ParseTimestamp(reader.GetString(2)), ReadNullableTimestamp(reader, 3), reader.GetInt64(4)));
        }
        var resources = new List<HostResource>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT resource_id,type,display_name,fingerprint,state,advertised_at,version FROM host_resources WHERE owner_principal_id=$owner AND host_id=$host ORDER BY resource_id;";
            command.Parameters.AddWithValue("$owner", owner);
            command.Parameters.AddWithValue("$host", hostId);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) resources.Add(new(owner, hostId, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), ParseTimestamp(reader.GetString(5)), reader.GetInt64(6)));
        }
        var resourceGrants = new List<HostResourceGrant>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT resource_id,access_mode,granted_at,revoked_at,version FROM host_resource_grants WHERE owner_principal_id=$owner AND host_id=$host ORDER BY resource_id,version;";
            command.Parameters.AddWithValue("$owner", owner);
            command.Parameters.AddWithValue("$host", hostId);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) resourceGrants.Add(new(owner, hostId, reader.GetString(0), reader.GetString(1), ParseTimestamp(reader.GetString(2)), ReadNullableTimestamp(reader, 3), reader.GetInt64(4)));
        }
        return new(host, capabilities.AsReadOnly(), capabilityGrants.AsReadOnly(), resources.AsReadOnly(), resourceGrants.AsReadOnly());
    }

    private static RemoteHost ReadHost(SqliteDataReader reader, string owner)
        => new(owner, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), RemoteHostValidation.NormalizeP256PublicJwk(reader.GetString(6)), reader.GetInt64(7), reader.GetString(8),
            reader.GetString(9), reader.GetString(10), reader.GetInt64(11), reader.GetInt64(12), ReadNullableTimestamp(reader, 13),
            ParseTimestamp(reader.GetString(14)), ReadNullableTimestamp(reader, 15), reader.GetInt64(16));

    private static void ValidateGrantRequests(
        IReadOnlyList<HostCapabilityGrantRequest> capabilities, IReadOnlyList<HostResourceGrantRequest> resources)
    {
        if (capabilities.Count > RemoteHostValidation.MaximumGrants || resources.Count > RemoteHostValidation.MaximumGrants)
            throw new ArgumentOutOfRangeException(nameof(capabilities));
        if (capabilities.Select(item => $"{item.CapabilityId}\n{item.CapabilityVersion}").Distinct(StringComparer.Ordinal).Count() != capabilities.Count
            || resources.Select(item => item.ResourceId).Distinct(StringComparer.Ordinal).Count() != resources.Count)
            throw new ArgumentException("Duplicate grant request.");
        foreach (var capability in capabilities)
        {
            if (capability.CapabilityId != RemoteHostValidation.SupportedCapabilityId
                || capability.CapabilityVersion != RemoteHostValidation.SupportedCapabilityVersion)
                throw new ArgumentException("Host capability grant is not supported.");
        }
        foreach (var resource in resources)
        {
            RemoteHostValidation.ValidateIdentifier(resource.ResourceId, nameof(resource.ResourceId));
            if (resource.AccessMode != RemoteHostValidation.ReadOnly)
                throw new ArgumentException("Resource access mode is not supported.");
        }
    }

    private static bool GrantsAreRequested(
        HostClaim claim, IReadOnlyList<HostCapabilityGrantRequest> capabilities, IReadOnlyList<HostResourceGrantRequest> resources)
        => capabilities.All(grant => claim.RequestedCapabilities.Any(requested =>
                requested.CapabilityId == grant.CapabilityId && requested.CapabilityVersion == grant.CapabilityVersion))
            && resources.All(grant => claim.RequestedResources.Any(requested => requested.ResourceId == grant.ResourceId));

    private static bool GrantsAreAdvertised(
        RemoteHostDetail detail, IReadOnlyList<HostCapabilityGrantRequest> capabilities, IReadOnlyList<HostResourceGrantRequest> resources)
        => capabilities.All(grant => detail.Capabilities.Any(advertised =>
                advertised.CapabilityId == grant.CapabilityId && advertised.CapabilityVersion == grant.CapabilityVersion))
            && resources.All(grant => detail.Resources.Any(advertised => advertised.ResourceId == grant.ResourceId));

    private static bool FixedAsciiEquals(string expected, string provided)
    {
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        var providedBytes = Encoding.ASCII.GetBytes(provided ?? string.Empty);
        return providedBytes.Length == expectedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }
}