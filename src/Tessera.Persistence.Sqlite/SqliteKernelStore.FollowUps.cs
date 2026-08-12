using System.Globalization;
using Microsoft.Data.Sqlite;
using Tessera.Core.Kernel;

namespace Tessera.Persistence.Sqlite;

public sealed partial class SqliteKernelStore
{
    public async Task<FollowUpOperationReceipt?> GetFollowUpOperationAsync(
        string ownerPrincipalId,
        string operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadOperationAsync(
            connection,
            null,
            ownerPrincipalId,
            operationId,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<FollowUpSourceReceipt?> GetFollowUpSourceAsync(
        string ownerPrincipalId,
        string sourceType,
        string sourceNativeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceNativeId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var source = await ReadSourceAsync(
            connection,
            null,
            ownerPrincipalId,
            sourceType,
            sourceNativeId,
            cancellationToken).ConfigureAwait(false);
        return source is null
            ? null
            : new FollowUpSourceReceipt(source.Value.PayloadHash, source.Value.FollowUpId, source.Value.ResultVersion);
    }

    public async Task RecordFollowUpOperationAsync(
        string ownerPrincipalId,
        string operationId,
        string requestHash,
        string followUpId,
        long resultVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(followUpId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var existing = await ReadOperationAsync(
            connection,
            transaction,
            ownerPrincipalId,
            operationId,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
            {
                throw new FollowUpOperationConflictException("Operation ID was already used with a different request.");
            }

            return;
        }

        await InsertOperationAsync(
            connection,
            transaction,
            ownerPrincipalId,
            operationId,
            requestHash,
            followUpId,
            resultVersion,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<FollowUp?> GetFollowUpAsync(
        string ownerPrincipalId,
        string followUpId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(followUpId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadFollowUpAsync(
            connection,
            null,
            ownerPrincipalId,
            followUpId,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FollowUp>> ListFollowUpsAsync(
        string ownerPrincipalId,
        FollowUpStatus? status = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);
        var boundedLimit = Math.Clamp(limit, 1, 101);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = status is null
            ? """
                SELECT follow_up_id
                FROM follow_ups
                WHERE owner_principal_id = $owner
                ORDER BY updated_at DESC, follow_up_id
                LIMIT $limit;
                """
            : """
                SELECT follow_up_id
                FROM follow_ups
                WHERE owner_principal_id = $owner AND status = $status
                ORDER BY updated_at DESC, follow_up_id
                LIMIT $limit;
                """;
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        command.Parameters.AddWithValue("$limit", boundedLimit);
        if (status is not null)
        {
            command.Parameters.AddWithValue("$status", status.Value.ToString());
        }

        var ids = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                ids.Add(reader.GetString(0));
            }
        }

        var followUps = new List<FollowUp>(ids.Count);
        foreach (var id in ids)
        {
            var followUp = await ReadFollowUpAsync(
                connection,
                null,
                ownerPrincipalId,
                id,
                cancellationToken).ConfigureAwait(false);
            if (followUp is not null)
            {
                followUps.Add(followUp);
            }
        }

        return followUps.AsReadOnly();
    }

    public async Task<FollowUpCommitResult> CommitFollowUpAsync(
        string ownerPrincipalId,
        FollowUpCommit commit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        EnsureOwner(ownerPrincipalId, commit.Aggregate.OwnerPrincipalId);
        EnsureOwner(ownerPrincipalId, commit.Evidence.OwnerPrincipalId);
        EnsureOwner(ownerPrincipalId, commit.ObservationEvent.OwnerPrincipalId);
        foreach (var assertion in commit.Assertions)
        {
            EnsureOwner(ownerPrincipalId, assertion.OwnerPrincipalId);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var operation = await ReadOperationAsync(
            connection,
            transaction,
            ownerPrincipalId,
            commit.OperationId,
            cancellationToken).ConfigureAwait(false);
        if (operation is not null)
        {
            if (!string.Equals(operation.RequestHash, commit.RequestHash, StringComparison.Ordinal))
            {
                throw new FollowUpOperationConflictException("Operation ID was already used with a different request.");
            }

            var replayed = await ReadFollowUpAsync(
                connection,
                transaction,
                ownerPrincipalId,
                operation.FollowUpId,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Operation references a missing FollowUp.");
            return new FollowUpCommitResult(replayed, true, operation.ResultVersion);
        }

        if (commit.SourceIdentity is not null)
        {
            var processedSource = await ReadSourceAsync(
                connection,
                transaction,
                ownerPrincipalId,
                commit.SourceIdentity.SourceType,
                commit.SourceIdentity.SourceNativeId,
                cancellationToken).ConfigureAwait(false);
            if (processedSource is not null)
            {
                if (!string.Equals(processedSource.Value.PayloadHash, commit.SourceIdentity.PayloadHash, StringComparison.Ordinal))
                {
                    throw new FollowUpOperationConflictException("Source identity was already used with a different payload.");
                }

                var replayed = await ReadFollowUpAsync(
                    connection,
                    transaction,
                    ownerPrincipalId,
                    processedSource.Value.FollowUpId,
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Processed source references a missing FollowUp.");
                await InsertOperationAsync(
                    connection,
                    transaction,
                    ownerPrincipalId,
                    commit.OperationId,
                    commit.RequestHash,
                    replayed.FollowUpId,
                    processedSource.Value.ResultVersion,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new FollowUpCommitResult(replayed, true, processedSource.Value.ResultVersion);
            }
        }

        var persistedVersion = await ReadFollowUpVersionAsync(
            connection,
            transaction,
            ownerPrincipalId,
            commit.Aggregate.FollowUpId,
            cancellationToken).ConfigureAwait(false);
        if (persistedVersion != commit.ExpectedVersion
            || commit.Aggregate.Version != (commit.ExpectedVersion ?? 0) + 1)
        {
            throw new FollowUpConcurrencyException("FollowUp version changed before commit.");
        }

        await UpsertFollowUpAsync(connection, transaction, commit.Aggregate, cancellationToken).ConfigureAwait(false);
        await ReplaceRevisionsAsync(connection, transaction, commit.Aggregate, cancellationToken).ConfigureAwait(false);
        await ReplaceTimelineAsync(connection, transaction, commit.Aggregate, cancellationToken).ConfigureAwait(false);
        await InsertEvidenceAsync(connection, transaction, commit.Evidence, cancellationToken).ConfigureAwait(false);
        await InsertEventAsync(connection, transaction, commit.ObservationEvent, cancellationToken).ConfigureAwait(false);
        foreach (var assertion in commit.Assertions)
        {
            await UpsertAssertionAsync(connection, transaction, assertion, cancellationToken).ConfigureAwait(false);
        }

        if (commit.SourceIdentity is not null)
        {
            await InsertSourceAsync(
                connection,
                transaction,
                ownerPrincipalId,
                commit.SourceIdentity,
                commit.Aggregate,
                cancellationToken).ConfigureAwait(false);
        }

        await InsertOperationAsync(
            connection,
            transaction,
            ownerPrincipalId,
            commit.OperationId,
            commit.RequestHash,
            commit.Aggregate.FollowUpId,
            commit.Aggregate.Version,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new FollowUpCommitResult(commit.Aggregate, false, commit.Aggregate.Version);
    }

    private static async Task<FollowUp?> ReadFollowUpAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string ownerPrincipalId,
        string followUpId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT status, created_at, updated_at, version
            FROM follow_ups
            WHERE owner_principal_id = $owner AND follow_up_id = $id;
            """;
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        command.Parameters.AddWithValue("$id", followUpId);
        FollowUpStatus status;
        DateTimeOffset createdAt;
        DateTimeOffset updatedAt;
        long version;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            status = Enum.Parse<FollowUpStatus>(reader.GetString(0));
            createdAt = ParseTimestamp(reader.GetString(1));
            updatedAt = ParseTimestamp(reader.GetString(2));
            version = reader.GetInt64(3);
        }

        var revisions = await ReadRevisionsAsync(
            connection,
            transaction,
            ownerPrincipalId,
            followUpId,
            cancellationToken).ConfigureAwait(false);
        var timeline = await ReadTimelineAsync(
            connection,
            transaction,
            ownerPrincipalId,
            followUpId,
            cancellationToken).ConfigureAwait(false);
        return FollowUp.Create(
            followUpId,
            ownerPrincipalId,
            status,
            revisions,
            timeline,
            createdAt,
            updatedAt,
            version);
    }

    private static async Task<List<FollowUpRevision>> ReadRevisionsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string ownerPrincipalId,
        string followUpId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revision_id, field, value, state, evidence_refs_json,
                   source_timestamp, parser_version, confidence,
                   correction_evidence_ref, lineage_revision_refs_json, created_at
            FROM follow_up_revisions
            WHERE owner_principal_id = $owner AND follow_up_id = $id
            ORDER BY created_at, revision_id;
            """;
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        command.Parameters.AddWithValue("$id", followUpId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var revisions = new List<FollowUpRevision>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            revisions.Add(FollowUpRevision.Create(
                reader.GetString(0),
                Enum.Parse<FollowUpField>(reader.GetString(1)),
                reader.GetString(2),
                Enum.Parse<FollowUpRevisionState>(reader.GetString(3)),
                FollowUpFieldProvenance.Create(
                    DeserializeReferences(reader.GetString(4)),
                    ParseTimestamp(reader.GetString(5)),
                    reader.GetString(6),
                    decimal.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                    ReadNullableString(reader, 8),
                    DeserializeReferences(reader.GetString(9))),
                ParseTimestamp(reader.GetString(10))));
        }

        return revisions;
    }

    private static async Task<List<FollowUpTimelineEntry>> ReadTimelineAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string ownerPrincipalId,
        string followUpId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sequence, kind, field, summary, evidence_ref, source_timestamp, recorded_at
            FROM follow_up_timeline
            WHERE owner_principal_id = $owner AND follow_up_id = $id
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        command.Parameters.AddWithValue("$id", followUpId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var timeline = new List<FollowUpTimelineEntry>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            timeline.Add(FollowUpTimelineEntry.Create(
                reader.GetInt64(0),
                Enum.Parse<FollowUpTimelineKind>(reader.GetString(1)),
                reader.IsDBNull(2) ? null : Enum.Parse<FollowUpField>(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4),
                ParseTimestamp(reader.GetString(5)),
                ParseTimestamp(reader.GetString(6))));
        }

        return timeline;
    }

    private static async Task<long?> ReadFollowUpVersionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string ownerPrincipalId,
        string followUpId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT version FROM follow_ups
            WHERE owner_principal_id = $owner AND follow_up_id = $id;
            """;
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        command.Parameters.AddWithValue("$id", followUpId);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<FollowUpOperationReceipt?> ReadOperationAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string ownerPrincipalId,
        string operationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT request_hash, follow_up_id, result_version
            FROM follow_up_operations
            WHERE owner_principal_id = $owner AND operation_id = $operation;
            """;
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        command.Parameters.AddWithValue("$operation", operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new FollowUpOperationReceipt(reader.GetString(0), reader.GetString(1), reader.GetInt64(2))
            : null;
    }

    private static async Task<(string FollowUpId, string PayloadHash, long ResultVersion)?> ReadSourceAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string ownerPrincipalId,
        string sourceType,
        string sourceNativeId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT follow_up_id, source_payload_hash, result_version
            FROM follow_up_sources
            WHERE owner_principal_id = $owner
              AND source_type = $sourceType
              AND source_native_id = $sourceNativeId;
            """;
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        command.Parameters.AddWithValue("$sourceType", sourceType);
        command.Parameters.AddWithValue("$sourceNativeId", sourceNativeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetString(0), reader.GetString(1), reader.GetInt64(2))
            : null;
    }

    private static async Task UpsertFollowUpAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FollowUp followUp,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO follow_ups(owner_principal_id, follow_up_id, status, created_at, updated_at, version)
            VALUES ($owner, $id, $status, $createdAt, $updatedAt, $version)
            ON CONFLICT(owner_principal_id, follow_up_id) DO UPDATE SET
                status = excluded.status,
                updated_at = excluded.updated_at,
                version = excluded.version;
            """;
        command.Parameters.AddWithValue("$owner", followUp.OwnerPrincipalId);
        command.Parameters.AddWithValue("$id", followUp.FollowUpId);
        command.Parameters.AddWithValue("$status", followUp.Status.ToString());
        command.Parameters.AddWithValue("$createdAt", FormatTimestamp(followUp.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", FormatTimestamp(followUp.UpdatedAt));
        command.Parameters.AddWithValue("$version", followUp.Version);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReplaceRevisionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FollowUp followUp,
        CancellationToken cancellationToken)
    {
        await DeleteChildrenAsync(connection, transaction, "follow_up_revisions", followUp, cancellationToken).ConfigureAwait(false);
        foreach (var revision in followUp.Revisions)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO follow_up_revisions(
                    owner_principal_id, follow_up_id, revision_id, field, value, state,
                    evidence_refs_json, source_timestamp, parser_version, confidence,
                    correction_evidence_ref, lineage_revision_refs_json, created_at)
                VALUES (
                    $owner, $followUpId, $revisionId, $field, $value, $state,
                    $evidenceRefs, $sourceTimestamp, $parserVersion, $confidence,
                    $correctionEvidenceRef, $lineageRefs, $createdAt);
                """;
            command.Parameters.AddWithValue("$owner", followUp.OwnerPrincipalId);
            command.Parameters.AddWithValue("$followUpId", followUp.FollowUpId);
            command.Parameters.AddWithValue("$revisionId", revision.RevisionId);
            command.Parameters.AddWithValue("$field", revision.Field.ToString());
            command.Parameters.AddWithValue("$value", revision.Value);
            command.Parameters.AddWithValue("$state", revision.State.ToString());
            command.Parameters.AddWithValue("$evidenceRefs", Serialize(revision.Provenance.EvidenceRefs));
            command.Parameters.AddWithValue("$sourceTimestamp", FormatTimestamp(revision.Provenance.SourceTimestamp));
            command.Parameters.AddWithValue("$parserVersion", revision.Provenance.ParserVersion);
            command.Parameters.AddWithValue("$confidence", revision.Provenance.Confidence.ToString(CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$correctionEvidenceRef", (object?)revision.Provenance.CorrectionEvidenceRef ?? DBNull.Value);
            command.Parameters.AddWithValue("$lineageRefs", Serialize(revision.Provenance.LineageRevisionRefs));
            command.Parameters.AddWithValue("$createdAt", FormatTimestamp(revision.CreatedAt));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReplaceTimelineAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FollowUp followUp,
        CancellationToken cancellationToken)
    {
        await DeleteChildrenAsync(connection, transaction, "follow_up_timeline", followUp, cancellationToken).ConfigureAwait(false);
        foreach (var entry in followUp.Timeline)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO follow_up_timeline(
                    owner_principal_id, follow_up_id, sequence, kind, field, summary,
                    evidence_ref, source_timestamp, recorded_at)
                VALUES (
                    $owner, $followUpId, $sequence, $kind, $field, $summary,
                    $evidenceRef, $sourceTimestamp, $recordedAt);
                """;
            command.Parameters.AddWithValue("$owner", followUp.OwnerPrincipalId);
            command.Parameters.AddWithValue("$followUpId", followUp.FollowUpId);
            command.Parameters.AddWithValue("$sequence", entry.Sequence);
            command.Parameters.AddWithValue("$kind", entry.Kind.ToString());
            command.Parameters.AddWithValue("$field", entry.Field is null ? DBNull.Value : entry.Field.Value.ToString());
            command.Parameters.AddWithValue("$summary", entry.Summary);
            command.Parameters.AddWithValue("$evidenceRef", entry.EvidenceRef);
            command.Parameters.AddWithValue("$sourceTimestamp", FormatTimestamp(entry.SourceTimestamp));
            command.Parameters.AddWithValue("$recordedAt", FormatTimestamp(entry.RecordedAt));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task DeleteChildrenAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        FollowUp followUp,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM {table} WHERE owner_principal_id = $owner AND follow_up_id = $id;";
        command.Parameters.AddWithValue("$owner", followUp.OwnerPrincipalId);
        command.Parameters.AddWithValue("$id", followUp.FollowUpId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertSourceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string ownerPrincipalId,
        FollowUpSourceIdentity source,
        FollowUp followUp,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO follow_up_sources(
                owner_principal_id, source_type, source_native_id, source_payload_hash,
                follow_up_id, result_version)
            VALUES ($owner, $sourceType, $sourceNativeId, $sourcePayloadHash,
                $followUpId, $resultVersion);
            """;
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        command.Parameters.AddWithValue("$sourceType", source.SourceType);
        command.Parameters.AddWithValue("$sourceNativeId", source.SourceNativeId);
        command.Parameters.AddWithValue("$sourcePayloadHash", source.PayloadHash);
        command.Parameters.AddWithValue("$followUpId", followUp.FollowUpId);
        command.Parameters.AddWithValue("$resultVersion", followUp.Version);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertOperationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string ownerPrincipalId,
        string operationId,
        string requestHash,
        string followUpId,
        long resultVersion,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO follow_up_operations(
                owner_principal_id, operation_id, request_hash, follow_up_id, result_version)
            VALUES ($owner, $operationId, $requestHash, $followUpId, $resultVersion);
            """;
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        command.Parameters.AddWithValue("$operationId", operationId);
        command.Parameters.AddWithValue("$requestHash", requestHash);
        command.Parameters.AddWithValue("$followUpId", followUpId);
        command.Parameters.AddWithValue("$resultVersion", resultVersion);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}