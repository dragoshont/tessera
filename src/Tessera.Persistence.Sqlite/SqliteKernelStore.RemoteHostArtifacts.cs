using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tessera.Core.Kernel;
using Tessera.Core.Product;

namespace Tessera.Persistence.Sqlite;

public sealed record HostArtifactListPage(
    IReadOnlyList<HostArtifact> Items,
    string? NextCursor);

public sealed record HostArtifactVerifyReceiptMutation(
    HostArtifactDetail? Artifact,
    string? EvidenceId,
    ProductIdempotencyReceipt? Receipt,
    bool Replayed,
    string? Error)
{
    public bool Succeeded => Error is null;
}

public sealed partial class SqliteKernelStore
{
    private const int RemoteArtifactPageLimit = 25;
    private const int RemoteArtifactPageLimitMax = 100;
    private const string HostArtifactEvidenceSourceType = "host.artifact";
    private static readonly ProducerRef HostArtifactProducer = ProducerRef.Create("remote-host", "1");

    public async Task<HostArtifactListPage> ListRunHostArtifactsAsync(
        string owner,
        string runId,
        int? limit = null,
        string? cursor = null,
        CancellationToken token = default)
    {
        RemoteHostValidation.ValidateIdentifier(runId, nameof(runId));
        var pageLimit = Math.Clamp(limit ?? RemoteArtifactPageLimit, 1, RemoteArtifactPageLimitMax);
        var parsedCursor = DecodeArtifactCursor(cursor);

        await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
        var items = await ReadArtifactPageByRunAsync(connection, null, owner, runId, pageLimit + 1, parsedCursor, token)
            .ConfigureAwait(false);
        var nextCursor = items.Count > pageLimit ? EncodeArtifactCursor(items[pageLimit - 1]) : null;
        if (items.Count > pageLimit)
            items.RemoveAt(pageLimit);
        return new(items, nextCursor);
    }

    public async Task<HostArtifactDetail?> GetHostArtifactDetailAsync(
        string owner,
        string artifactId,
        CancellationToken token = default)
    {
        RemoteHostValidation.ValidateIdentifier(artifactId, nameof(artifactId));

        await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
        return await ReadHostArtifactDetailAsync(connection, null, owner, artifactId, token)
            .ConfigureAwait(false);
    }

    public async Task<HostArtifactVerifyReceiptMutation> VerifyHostArtifactAsync(
        string owner,
        string artifactId,
        long expectedVersion,
        string idempotencyKey,
        string requestHash,
        DateTimeOffset now,
        CancellationToken token = default)
    {
        const string routeFamily = "host-artifact-verify";
        RemoteHostValidation.ValidateIdentifier(artifactId, nameof(artifactId));
        RemoteHostValidation.ValidateIdentifier(idempotencyKey, nameof(idempotencyKey));
        RemoteHostValidation.ValidateLowerHex(requestHash, 64, nameof(requestHash));
        requestHash = BindTargetRequestHash(artifactId, requestHash);

        await using var connection = await OpenConnectionAsync(token).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var prior = await ReadReceiptAsync(connection, transaction, owner, routeFamily, idempotencyKey, token)
            .ConfigureAwait(false);
        if (prior is not null)
        {
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return string.Equals(prior.RequestHash, requestHash, StringComparison.Ordinal)
                ? new(null, null, prior, true, null)
                : new(null, null, null, false, "idempotency_conflict");
        }

        var detail = await ReadHostArtifactDetailAsync(connection, transaction, owner, artifactId, token)
            .ConfigureAwait(false);
        if (detail is null)
            return await RejectHostArtifactAsync(connection, transaction, owner, artifactId, routeFamily, idempotencyKey, requestHash, 404, "artifact_not_found", now, token)
                .ConfigureAwait(false);
        if (detail.Artifact.Version != expectedVersion)
            return await RejectHostArtifactAsync(connection, transaction, owner, artifactId, routeFamily, idempotencyKey, requestHash, 409, "artifact_version_conflict", now, token)
                .ConfigureAwait(false);

        var lease = await ReadLeaseAsync(connection, transaction, owner, detail.Artifact.LeaseId, token).ConfigureAwait(false);
        if (lease is null || lease.RunId != detail.Artifact.RunId)
            return await RejectHostArtifactAsync(connection, transaction, owner, artifactId, routeFamily, idempotencyKey, requestHash, 409, "artifact_not_found", now, token)
                .ConfigureAwait(false);
        if (lease.State == HostLeaseStates.Expired
            && lease.FailureCode == "reconciled_not_started")
        {
            return await RejectHostArtifactAsync(connection, transaction, owner, artifactId, routeFamily, idempotencyKey, requestHash, 409, "artifact_version_conflict", now, token)
                .ConfigureAwait(false);
        }
        if (!await HasValidHostArtifactReceiptAsync(connection, transaction, detail.Artifact, lease.HostId, token).ConfigureAwait(false))
            return await RejectHostArtifactAsync(connection, transaction, owner, artifactId, routeFamily, idempotencyKey, requestHash, 409, "artifact_version_conflict", now, token)
                .ConfigureAwait(false);

        var persistedBytes = Encoding.UTF8.GetBytes(detail.TextContent);
        var persistedHash = Convert.ToHexStringLower(SHA256.HashData(persistedBytes));
        if (persistedBytes.Length != detail.Artifact.SizeBytes
            || !string.Equals(persistedHash, detail.Artifact.Sha256, StringComparison.Ordinal))
        {
            return await RejectHostArtifactAsync(connection, transaction, owner, artifactId, routeFamily, idempotencyKey, requestHash, 409, "artifact_version_conflict", now, token)
                .ConfigureAwait(false);
        }

        var evidence = await ReadHostArtifactEvidenceAsync(connection, transaction, owner, artifactId, token).ConfigureAwait(false);
        if (evidence is not null && !MatchesHostArtifactEvidence(evidence, detail.Artifact))
        {
            return await RejectHostArtifactAsync(connection, transaction, owner, artifactId, routeFamily, idempotencyKey, requestHash, 409, "artifact_version_conflict", now, token)
                .ConfigureAwait(false);
        }

        if (evidence is null)
        {
            var evidenceId = HostArtifactEvidenceId(artifactId);
            if (await ReadEvidenceByIdAsync(connection, transaction, owner, evidenceId, token).ConfigureAwait(false) is not null)
            {
                return await RejectHostArtifactAsync(connection, transaction, owner, artifactId, routeFamily, idempotencyKey, requestHash, 409, "artifact_version_conflict", now, token)
                    .ConfigureAwait(false);
            }

            evidence = EvidenceRecord.Create(
                evidenceId,
                owner,
                HostArtifactEvidenceSourceType,
                artifactId,
                $"host-artifact:{artifactId}",
                now,
                detail.Artifact.CreatedAt,
                "SHA-256",
                1,
                detail.Artifact.Sha256,
                RetentionState.Active,
                SensitivityClass.Confidential,
                HostArtifactProducer,
                1,
                boundedExcerpt: null,
                contentReference: artifactId);
            await InsertEvidenceAsync(connection, transaction, evidence, token).ConfigureAwait(false);
        }

        var receipt = CreateRemoteHostReceipt(
            owner,
            routeFamily,
            idempotencyKey,
            requestHash,
            200,
            SerializeVerifiedArtifact(detail.Artifact, evidence.EvidenceId),
            "host_artifact",
            artifactId,
            now);
        await CommitRemoteHostMutationAsync(connection, transaction, receipt, token).ConfigureAwait(false);
        return new(detail with { EvidenceId = evidence.EvidenceId }, evidence.EvidenceId, receipt, false, null);
    }

    internal static async Task<HostMessageBusinessResponse> UploadHostArtifactAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RemoteHost host,
        string leaseId,
        long leaseVersion,
        string localAttemptId,
        string artifactId,
        string kind,
        string mediaType,
        string summary,
        long declaredSize,
        string declaredSha256,
        string retention,
        string textContent,
        string messageId,
        DateTimeOffset now,
        CancellationToken token)
    {
        return await GuardHostRequestAsync(async () =>
        {
            RemoteHostValidation.ValidateIdentifier(artifactId, nameof(artifactId));
            RemoteHostValidation.ValidateIdentifier(localAttemptId, nameof(localAttemptId));
            RemoteHostValidation.ValidateIdentifier(messageId, nameof(messageId));
            if (!HostArtifactKinds.IsValid(kind)
                || !HostArtifactMediaTypes.IsValid(mediaType)
                || !HostArtifactRetentions.IsValid(retention)
                || declaredSize < 0
                || declaredSize > RemoteHostProtocol.MaximumArtifactBodyBytes)
            {
                return HostLeaseProblem(400, "host_invalid_request");
            }

            RemoteHostValidation.ValidateLowerHex(declaredSha256, 64, nameof(declaredSha256));
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
                allowedStates: [HostLeaseStates.Acknowledged, HostLeaseStates.Running],
                token).ConfigureAwait(false);
            if (leaseValidation.Problem is not null)
                return leaseValidation.Problem;

            var lease = leaseValidation.Lease!;
            var normalizedSummary = NormalizeArtifactSummary(summary);
            var normalizedContent = RemoteHostOutputNormalizer.Normalize(
                Encoding.UTF8.GetBytes(textContent),
                RemoteHostProtocol.MaximumArtifactBodyBytes);
            if (declaredSize != normalizedContent.SizeBytes
                || !string.Equals(declaredSha256, normalizedContent.Sha256, StringComparison.Ordinal))
            {
                return HostLeaseProblem(409, "artifact_hash_mismatch");
            }

            if (await ReadHostArtifactAsync(connection, transaction, lease.OwnerPrincipalId, artifactId, token).ConfigureAwait(false) is not null)
                return HostLeaseProblem(409, "artifact_conflict");
            if (await CountHostArtifactsByRunAsync(connection, transaction, lease.OwnerPrincipalId, lease.RunId, token).ConfigureAwait(false)
                >= RemoteHostValidation.MaximumArtifactsPerRun)
            {
                return HostLeaseProblem(409, "artifact_limit_exceeded");
            }

            var artifact = new HostArtifact(
                lease.OwnerPrincipalId,
                artifactId,
                lease.RunId,
                lease.LeaseId,
                null,
                HostArtifactKinds.Text,
                HostArtifactMediaTypes.TextPlain,
                normalizedSummary.Text,
                normalizedContent.SizeBytes,
                normalizedContent.Sha256,
                HostArtifactRetentions.Run,
                HostArtifactContentStates.Available,
                normalizedContent.Redacted,
                normalizedContent.Truncated,
                now,
                null,
                1);
            await InsertHostArtifactAsync(
                connection,
                transaction,
                artifact,
                normalizedContent.Text,
                messageId,
                (int)declaredSize,
                declaredSha256,
                now,
                token).ConfigureAwait(false);
            return new(201, SerializeUploadedArtifact(artifact));
        }).ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyList<HostArtifact>> ListArtifactsByRunAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string owner,
        string runId,
        CancellationToken token)
        => await ReadAllArtifactsByRunAsync(connection, transaction, owner, runId, token).ConfigureAwait(false);

    private static async Task<bool> HasHostArtifactForLeaseAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string owner,
        string leaseId,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM host_artifacts WHERE owner_principal_id=$owner AND lease_id=$lease);";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$lease", leaseId);
        return (long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))! == 1;
    }

    private async Task<HostArtifactVerifyReceiptMutation> RejectHostArtifactAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string owner,
        string artifactId,
        string routeFamily,
        string idempotencyKey,
        string requestHash,
        int status,
        string error,
        DateTimeOffset now,
        CancellationToken token)
    {
        var receipt = CreateRemoteHostReceipt(
            owner,
            routeFamily,
            idempotencyKey,
            requestHash,
            status,
            RemoteHostSnapshotSerializer.SerializeProblem(status, error),
            "host_artifact",
            artifactId,
            now);
        await CommitRemoteHostMutationAsync(connection, transaction, receipt, token).ConfigureAwait(false);
        return new(null, null, receipt, false, error);
    }

    private static async Task InsertHostArtifactAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        HostArtifact artifact,
        string textContent,
        string messageId,
        int declaredSize,
        string declaredSha256,
        DateTimeOffset now,
        CancellationToken token)
    {
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO host_artifacts(
                    owner_principal_id,artifact_id,run_id,lease_id,action_id,kind,media_type,
                    summary,size_bytes,sha256,retention,content_state,redacted,truncated,
                    created_at,expires_at,version)
                VALUES(
                    $owner,$artifact,$run,$lease,$action,$kind,$mediaType,$summary,$sizeBytes,$sha256,
                    $retention,$contentState,$redacted,$truncated,$createdAt,$expiresAt,$version);
                """;
            command.Parameters.AddWithValue("$owner", artifact.OwnerPrincipalId);
            command.Parameters.AddWithValue("$artifact", artifact.ArtifactId);
            command.Parameters.AddWithValue("$run", artifact.RunId);
            command.Parameters.AddWithValue("$lease", artifact.LeaseId);
            command.Parameters.AddWithValue("$action", DBNull.Value);
            command.Parameters.AddWithValue("$kind", artifact.Kind);
            command.Parameters.AddWithValue("$mediaType", artifact.MediaType);
            command.Parameters.AddWithValue("$summary", artifact.Summary);
            command.Parameters.AddWithValue("$sizeBytes", artifact.SizeBytes);
            command.Parameters.AddWithValue("$sha256", artifact.Sha256);
            command.Parameters.AddWithValue("$retention", artifact.Retention);
            command.Parameters.AddWithValue("$contentState", artifact.ContentState);
            command.Parameters.AddWithValue("$redacted", artifact.Redacted ? 1 : 0);
            command.Parameters.AddWithValue("$truncated", artifact.Truncated ? 1 : 0);
            command.Parameters.AddWithValue("$createdAt", FormatTimestamp(artifact.CreatedAt));
            command.Parameters.AddWithValue("$expiresAt", DBNull.Value);
            command.Parameters.AddWithValue("$version", artifact.Version);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO host_artifact_contents(owner_principal_id,artifact_id,text_content)
                VALUES($owner,$artifact,$text);
                """;
            command.Parameters.AddWithValue("$owner", artifact.OwnerPrincipalId);
            command.Parameters.AddWithValue("$artifact", artifact.ArtifactId);
            command.Parameters.AddWithValue("$text", textContent);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO host_artifact_receipts(
                    owner_principal_id,receipt_id,artifact_id,message_id,declared_size,declared_sha256,accepted_at)
                VALUES($owner,$receipt,$artifact,$message,$size,$sha256,$acceptedAt);
                """;
            command.Parameters.AddWithValue("$owner", artifact.OwnerPrincipalId);
            command.Parameters.AddWithValue("$receipt", artifact.ArtifactId);
            command.Parameters.AddWithValue("$artifact", artifact.ArtifactId);
            command.Parameters.AddWithValue("$message", messageId);
            command.Parameters.AddWithValue("$size", declaredSize);
            command.Parameters.AddWithValue("$sha256", declaredSha256);
            command.Parameters.AddWithValue("$acceptedAt", FormatTimestamp(now));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }

    private static async Task<List<HostArtifact>> ReadArtifactPageByRunAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string owner,
        string runId,
        int limit,
        HostArtifactCursor? cursor,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = cursor is null
            ? """
                SELECT artifact.owner_principal_id,artifact.artifact_id,artifact.run_id,artifact.lease_id,
                       artifact.action_id,artifact.kind,artifact.media_type,artifact.summary,
                       artifact.size_bytes,artifact.sha256,artifact.retention,artifact.content_state,
                       artifact.redacted,artifact.truncated,artifact.created_at,artifact.expires_at,
                       artifact.version
                FROM host_artifacts artifact
                WHERE artifact.owner_principal_id=$owner AND artifact.run_id=$run
                ORDER BY artifact.created_at DESC, artifact.artifact_id
                LIMIT $limit;
                """
            : """
                SELECT artifact.owner_principal_id,artifact.artifact_id,artifact.run_id,artifact.lease_id,
                       artifact.action_id,artifact.kind,artifact.media_type,artifact.summary,
                       artifact.size_bytes,artifact.sha256,artifact.retention,artifact.content_state,
                       artifact.redacted,artifact.truncated,artifact.created_at,artifact.expires_at,
                       artifact.version
                FROM host_artifacts artifact
                WHERE artifact.owner_principal_id=$owner AND artifact.run_id=$run
                  AND (artifact.created_at < $cursorCreatedAt
                       OR (artifact.created_at = $cursorCreatedAt AND artifact.artifact_id > $cursorArtifactId))
                ORDER BY artifact.created_at DESC, artifact.artifact_id
                LIMIT $limit;
                """;
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$cursorCreatedAt", cursor is null ? DBNull.Value : FormatTimestamp(cursor.CreatedAt));
        command.Parameters.AddWithValue("$cursorArtifactId", cursor is null ? DBNull.Value : cursor.ArtifactId);
        var items = new List<HostArtifact>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            items.Add(ReadHostArtifact(reader));
        return items;
    }

    private static async Task<IReadOnlyList<HostArtifact>> ReadAllArtifactsByRunAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string owner,
        string runId,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT artifact.owner_principal_id,artifact.artifact_id,artifact.run_id,artifact.lease_id,
                   artifact.action_id,artifact.kind,artifact.media_type,artifact.summary,
                   artifact.size_bytes,artifact.sha256,artifact.retention,artifact.content_state,
                   artifact.redacted,artifact.truncated,artifact.created_at,artifact.expires_at,
                   artifact.version
            FROM host_artifacts artifact
            WHERE artifact.owner_principal_id=$owner AND artifact.run_id=$run
            ORDER BY artifact.created_at DESC, artifact.artifact_id;
            """;
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$run", runId);
        var items = new List<HostArtifact>();
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            items.Add(ReadHostArtifact(reader));
        return items;
    }

    private static async Task<HostArtifact?> ReadHostArtifactAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string owner,
        string artifactId,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT artifact.owner_principal_id,artifact.artifact_id,artifact.run_id,artifact.lease_id,
                   artifact.action_id,artifact.kind,artifact.media_type,artifact.summary,
                   artifact.size_bytes,artifact.sha256,artifact.retention,artifact.content_state,
                   artifact.redacted,artifact.truncated,artifact.created_at,artifact.expires_at,
                   artifact.version
            FROM host_artifacts artifact
            WHERE artifact.owner_principal_id=$owner AND artifact.artifact_id=$artifact;
            """;
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$artifact", artifactId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadHostArtifact(reader) : null;
    }

    private static async Task<long> CountHostArtifactsByRunAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string owner,
        string runId,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM host_artifacts WHERE owner_principal_id=$owner AND run_id=$run;";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$run", runId);
        return (long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
    }

    private static async Task<HostArtifactDetail?> ReadHostArtifactDetailAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string owner,
        string artifactId,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
                command.CommandText = """
                        SELECT artifact.owner_principal_id,artifact.artifact_id,artifact.run_id,artifact.lease_id,
                                     artifact.action_id,artifact.kind,artifact.media_type,artifact.summary,
                                     artifact.size_bytes,artifact.sha256,artifact.retention,artifact.content_state,
                                     artifact.redacted,artifact.truncated,artifact.created_at,artifact.expires_at,
                                     artifact.version,content.text_content
            FROM host_artifacts artifact
            JOIN host_artifact_contents content
              ON content.owner_principal_id=artifact.owner_principal_id
             AND content.artifact_id=artifact.artifact_id
            WHERE artifact.owner_principal_id=$owner AND artifact.artifact_id=$artifact;
            """;
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$artifact", artifactId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false))
            return null;
        var artifact = ReadHostArtifact(reader);
        return new(artifact, reader.GetString(17), null);
    }

    private static async Task<EvidenceRecord?> ReadEvidenceByIdAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string owner,
        string evidenceId,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = EvidenceSelect + " WHERE owner_principal_id=$owner AND evidence_id=$id;";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$id", evidenceId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadEvidence(reader) : null;
    }

    private static async Task<EvidenceRecord?> ReadHostArtifactEvidenceAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string owner,
        string artifactId,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = EvidenceSelect + " WHERE owner_principal_id=$owner AND source_type=$sourceType AND source_native_id=$sourceNativeId;";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$sourceType", HostArtifactEvidenceSourceType);
        command.Parameters.AddWithValue("$sourceNativeId", artifactId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadEvidence(reader) : null;
    }

    private static async Task<bool> HasValidHostArtifactReceiptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        HostArtifact artifact,
        string hostId,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT receipt.declared_size,receipt.declared_sha256,
                   message.operation,message.target_id,message.response_status
            FROM host_artifact_receipts receipt
            JOIN host_accepted_messages message
              ON message.owner_principal_id=receipt.owner_principal_id
             AND message.host_id=$host
             AND message.message_id=receipt.message_id
            WHERE receipt.owner_principal_id=$owner AND receipt.artifact_id=$artifact;
            """;
        command.Parameters.AddWithValue("$owner", artifact.OwnerPrincipalId);
        command.Parameters.AddWithValue("$artifact", artifact.ArtifactId);
        command.Parameters.AddWithValue("$host", hostId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false)
            && reader.GetInt64(0) == artifact.SizeBytes
            && reader.GetString(1) == artifact.Sha256
            && reader.GetString(2) == HostAcceptedMessageOperations.LeaseArtifact
            && reader.GetString(3) == artifact.LeaseId
            && reader.GetInt32(4) == 201;
    }

    private static bool MatchesHostArtifactEvidence(EvidenceRecord evidence, HostArtifact artifact)
        => evidence.OwnerPrincipalId == artifact.OwnerPrincipalId
            && evidence.SourceType == HostArtifactEvidenceSourceType
            && evidence.SourceNativeId == artifact.ArtifactId
            && evidence.SourceLocator == $"host-artifact:{artifact.ArtifactId}"
            && evidence.SourceTimestamp == artifact.CreatedAt
            && evidence.ContentHashAlgorithm == "SHA-256"
            && evidence.ContentHashVersion == 1
            && evidence.ContentHash == artifact.Sha256
            && evidence.RetentionState == RetentionState.Active
            && evidence.Sensitivity == SensitivityClass.Confidential
            && evidence.Producer == HostArtifactProducer
            && evidence.SchemaVersion == 1
            && evidence.BoundedExcerpt is null
            && evidence.ContentReference == artifact.ArtifactId;

    private static NormalizedRemoteHostOutput NormalizeArtifactSummary(string summary)
        => RemoteHostOutputNormalizer.Normalize(
            Encoding.UTF8.GetBytes(summary ?? string.Empty),
            RemoteHostValidation.MaximumArtifactSummaryBytes);

    private static string SerializeUploadedArtifact(HostArtifact artifact)
        => JsonSerializer.Serialize(new
        {
            artifact = ToArtifactSnapshot(artifact),
            replayed = false,
        });

    private static string SerializeVerifiedArtifact(HostArtifact artifact, string evidenceId)
        => JsonSerializer.Serialize(new
        {
            artifact = ToArtifactSnapshot(artifact),
            evidenceId,
        });

    private static object ToArtifactSnapshot(HostArtifact artifact)
        => new
        {
            artifactId = artifact.ArtifactId,
            runId = artifact.RunId,
            leaseId = artifact.LeaseId,
            actionId = artifact.ActionId,
            kind = artifact.Kind,
            mediaType = artifact.MediaType,
            summary = artifact.Summary,
            sizeBytes = artifact.SizeBytes,
            sha256 = artifact.Sha256,
            retention = artifact.Retention,
            contentState = artifact.ContentState,
            redacted = artifact.Redacted,
            truncated = artifact.Truncated,
            createdAt = artifact.CreatedAt,
            expiresAt = artifact.ExpiresAt,
            version = artifact.Version,
        };

    private static HostArtifact ReadHostArtifact(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            ReadNullableString(reader, 4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetInt32(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetInt64(12) == 1,
            reader.GetInt64(13) == 1,
            ParseTimestamp(reader.GetString(14)),
            ReadNullableTimestamp(reader, 15),
            reader.GetInt64(16));

    private static string HostArtifactEvidenceId(string artifactId)
        => $"evidence:host-artifact:{artifactId}";

    private static string EncodeArtifactCursor(HostArtifact artifact)
        => Base64UrlEncode(Encoding.UTF8.GetBytes($"{FormatTimestamp(artifact.CreatedAt)}\n{artifact.ArtifactId}"));

    private static HostArtifactCursor? DecodeArtifactCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return null;
        try
        {
            var value = Encoding.UTF8.GetString(Base64UrlDecode(cursor));
            var separator = value.IndexOf('\n');
            if (separator <= 0 || separator == value.Length - 1)
                throw new ArgumentException("Artifact cursor is invalid.", nameof(cursor));
            var createdAt = ParseTimestamp(value[..separator]);
            var artifactId = value[(separator + 1)..];
            RemoteHostValidation.ValidateIdentifier(artifactId, nameof(cursor));
            return new(createdAt, artifactId);
        }
        catch (FormatException)
        {
            throw new ArgumentException("Artifact cursor is invalid.", nameof(cursor));
        }
    }

    private static string Base64UrlEncode(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
        return Convert.FromBase64String(padded);
    }

    private sealed record HostArtifactCursor(DateTimeOffset CreatedAt, string ArtifactId);
}
