using System.Globalization;
using Microsoft.Data.Sqlite;
using Tessera.Core.Kernel;

namespace Tessera.Persistence.Sqlite;

public sealed partial class SqliteKernelStore
{
    public async Task AddAsync(
        PrincipalRef principal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO principals(principal_id, issuer, tenant, subject, display_hint, created_at)
            VALUES ($id, $issuer, $tenant, $subject, $displayHint, $createdAt)
            ON CONFLICT(principal_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", principal.PrincipalId);
        command.Parameters.AddWithValue("$issuer", principal.Issuer);
        command.Parameters.AddWithValue("$tenant", principal.Tenant);
        command.Parameters.AddWithValue("$subject", principal.Subject);
        command.Parameters.AddWithValue("$displayHint", (object?)principal.DisplayHint ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", FormatTimestamp(principal.CreatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PrincipalRef?> GetAsync(
        string ownerPrincipalId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT issuer, tenant, subject, display_hint, created_at
            FROM principals
            WHERE principal_id = $owner;
            """;
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var principal = PrincipalRef.Create(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            ReadNullableString(reader, 3),
            ParseTimestamp(reader.GetString(4)));
        if (!string.Equals(principal.PrincipalId, ownerPrincipalId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Persisted principal identity does not match its canonical identifier.");
        }

        return principal;
    }

    public async Task AddAsync(
        string ownerPrincipalId,
        EvidenceRecord evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        EnsureOwner(ownerPrincipalId, evidence.OwnerPrincipalId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await InsertEvidenceAsync(connection, null, evidence, cancellationToken).ConfigureAwait(false);
    }

    Task<EvidenceRecord?> IEvidenceRepository.GetAsync(
        string ownerPrincipalId,
        string evidenceId,
        CancellationToken cancellationToken)
        => GetEvidenceAsync(ownerPrincipalId, evidenceId, cancellationToken);

    public async Task<EvidenceRecord?> GetEvidenceAsync(
        string ownerPrincipalId,
        string evidenceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{EvidenceSelect} WHERE owner_principal_id = $owner AND evidence_id = $id;";
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        command.Parameters.AddWithValue("$id", evidenceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadEvidence(reader)
            : null;
    }

    async Task<IReadOnlyList<EvidenceRecord>> IEvidenceRepository.ListAsync(
        string ownerPrincipalId,
        CancellationToken cancellationToken)
        => await ListEvidenceAsync(ownerPrincipalId, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<EvidenceRecord>> ListEvidenceAsync(
        string ownerPrincipalId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{EvidenceSelect} WHERE owner_principal_id = $owner ORDER BY observed_at DESC, evidence_id;";
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<EvidenceRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(ReadEvidence(reader));
        }

        return records.AsReadOnly();
    }

    public async Task<bool> UpdateRetentionAsync(
        string ownerPrincipalId,
        string evidenceId,
        RetentionState retentionState,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE evidence
            SET retention_state = $retentionState
            WHERE owner_principal_id = $owner AND evidence_id = $id;
            """;
        command.Parameters.AddWithValue("$retentionState", retentionState.ToString());
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        command.Parameters.AddWithValue("$id", evidenceId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task AppendAsync(
        string ownerPrincipalId,
        ObservationEvent observationEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observationEvent);
        EnsureOwner(ownerPrincipalId, observationEvent.OwnerPrincipalId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await InsertEventAsync(connection, null, observationEvent, cancellationToken).ConfigureAwait(false);
    }

    async Task<IReadOnlyList<ObservationEvent>> IEventRepository.ListAsync(
        string ownerPrincipalId,
        CancellationToken cancellationToken)
        => await ListEventsAsync(ownerPrincipalId, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<ObservationEvent>> ListEventsAsync(
        string ownerPrincipalId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{EventSelect} WHERE owner_principal_id = $owner ORDER BY occurred_at, event_id;";
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<ObservationEvent>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(ReadEvent(reader));
        }

        return records.AsReadOnly();
    }

    public async Task SaveBatchAsync(
        string ownerPrincipalId,
        IReadOnlyCollection<AssertionRecord> assertions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assertions);
        foreach (var assertion in assertions)
        {
            EnsureOwner(ownerPrincipalId, assertion.OwnerPrincipalId);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var assertion in assertions
            .OrderBy(assertion => assertion.EpistemicStatus == EpistemicStatus.Current ? 1 : 0))
        {
            await UpsertAssertionAsync(connection, transaction, assertion, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyCorrectionAsync(
        string ownerPrincipalId,
        AssertionRecord superseded,
        AssertionRecord current,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(superseded);
        ArgumentNullException.ThrowIfNull(current);
        EnsureOwner(ownerPrincipalId, superseded.OwnerPrincipalId);
        EnsureOwner(ownerPrincipalId, current.OwnerPrincipalId);
        if (superseded.EpistemicStatus != EpistemicStatus.Superseded
            || current.EpistemicStatus != EpistemicStatus.Current
            || string.Equals(superseded.AssertionId, current.AssertionId, StringComparison.Ordinal)
            || !string.Equals(superseded.SubjectKey, current.SubjectKey, StringComparison.Ordinal)
            || !string.Equals(superseded.Predicate, current.Predicate, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Correction persistence requires superseded/current assertions for one owner and key.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await UpsertAssertionAsync(connection, transaction, superseded, cancellationToken).ConfigureAwait(false);
        await UpsertAssertionAsync(connection, transaction, current, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    Task<AssertionRecord?> IAssertionRepository.GetAsync(
        string ownerPrincipalId,
        string assertionId,
        CancellationToken cancellationToken)
        => GetAssertionAsync(ownerPrincipalId, assertionId, cancellationToken);

    public async Task<AssertionRecord?> GetAssertionAsync(
        string ownerPrincipalId,
        string assertionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(assertionId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{AssertionSelect} WHERE owner_principal_id = $owner AND assertion_id = $id;";
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        command.Parameters.AddWithValue("$id", assertionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadAssertion(reader)
            : null;
    }

    public async Task<IReadOnlyList<AssertionRecord>> ListCurrentAsync(
        string ownerPrincipalId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            {AssertionSelect}
            WHERE owner_principal_id = $owner AND epistemic_status = $status
            ORDER BY subject_key, predicate, valid_from DESC, assertion_id;
            """;
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        command.Parameters.AddWithValue("$status", EpistemicStatus.Current.ToString());
        return await ReadAssertionsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AssertionRecord>> ListHistoryAsync(
        string ownerPrincipalId,
        string subjectKey,
        string predicate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(predicate);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            {AssertionSelect}
            WHERE owner_principal_id = $owner AND subject_key = $subjectKey AND predicate = $predicate
            ORDER BY created_at, assertion_id;
            """;
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        command.Parameters.AddWithValue("$subjectKey", subjectKey);
        command.Parameters.AddWithValue("$predicate", predicate);
        return await ReadAssertionsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task AddObservationAsync(
        string ownerPrincipalId,
        EvidenceRecord evidence,
        ObservationEvent observationEvent,
        AssertionRecord candidateAssertion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(observationEvent);
        ArgumentNullException.ThrowIfNull(candidateAssertion);
        EnsureOwner(ownerPrincipalId, evidence.OwnerPrincipalId);
        EnsureOwner(ownerPrincipalId, observationEvent.OwnerPrincipalId);
        EnsureOwner(ownerPrincipalId, candidateAssertion.OwnerPrincipalId);
        if (candidateAssertion.EpistemicStatus != EpistemicStatus.Candidate)
        {
            throw new InvalidOperationException("Observation ingestion accepts a candidate assertion, not current belief.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await InsertEvidenceAsync(connection, transaction, evidence, cancellationToken).ConfigureAwait(false);
        await InsertEventAsync(connection, transaction, observationEvent, cancellationToken).ConfigureAwait(false);
        await UpsertAssertionAsync(connection, transaction, candidateAssertion, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string EvidenceSelect = """
        SELECT evidence_id, owner_principal_id, source_type, source_native_id, source_locator,
               observed_at, source_timestamp, hash_algorithm, hash_version, content_hash,
               retention_state, sensitivity, producer_id, producer_version, schema_version,
               bounded_excerpt, content_reference
        FROM evidence
        """;

    private const string EventSelect = """
        SELECT event_id, owner_principal_id, event_type, occurred_at, observed_at,
               actor_refs_json, object_refs_json, evidence_refs_json, attributes_json,
               producer_id, producer_version, schema_version
        FROM observation_events
        """;

    private const string AssertionSelect = """
        SELECT assertion_id, owner_principal_id, subject_key, predicate, value,
               assertion_type, epistemic_status, confidence, valid_from, valid_to,
               created_at, superseded_at, evidence_refs_json, lineage_refs_json,
               promotion_reason, producer_id, producer_version, schema_version
        FROM assertions
        """;

    private static async Task InsertEvidenceAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        EvidenceRecord evidence,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO evidence(
                evidence_id, owner_principal_id, source_type, source_native_id, source_locator,
                observed_at, source_timestamp, hash_algorithm, hash_version, content_hash,
                retention_state, sensitivity, producer_id, producer_version, schema_version,
                bounded_excerpt, content_reference)
            VALUES (
                $id, $owner, $sourceType, $sourceNativeId, $sourceLocator,
                $observedAt, $sourceTimestamp, $hashAlgorithm, $hashVersion, $contentHash,
                $retentionState, $sensitivity, $producerId, $producerVersion, $schemaVersion,
                $boundedExcerpt, $contentReference);
            """;
        command.Parameters.AddWithValue("$id", evidence.EvidenceId);
        command.Parameters.AddWithValue("$owner", evidence.OwnerPrincipalId);
        command.Parameters.AddWithValue("$sourceType", evidence.SourceType);
        command.Parameters.AddWithValue("$sourceNativeId", evidence.SourceNativeId);
        command.Parameters.AddWithValue("$sourceLocator", evidence.SourceLocator);
        command.Parameters.AddWithValue("$observedAt", FormatTimestamp(evidence.ObservedAt));
        command.Parameters.AddWithValue("$sourceTimestamp", evidence.SourceTimestamp is null
            ? DBNull.Value
            : FormatTimestamp(evidence.SourceTimestamp.Value));
        command.Parameters.AddWithValue("$hashAlgorithm", evidence.ContentHashAlgorithm);
        command.Parameters.AddWithValue("$hashVersion", evidence.ContentHashVersion);
        command.Parameters.AddWithValue("$contentHash", evidence.ContentHash);
        command.Parameters.AddWithValue("$retentionState", evidence.RetentionState.ToString());
        command.Parameters.AddWithValue("$sensitivity", evidence.Sensitivity.ToString());
        command.Parameters.AddWithValue("$producerId", evidence.Producer.Id);
        command.Parameters.AddWithValue("$producerVersion", evidence.Producer.Version);
        command.Parameters.AddWithValue("$schemaVersion", evidence.SchemaVersion);
        command.Parameters.AddWithValue("$boundedExcerpt", (object?)evidence.BoundedExcerpt ?? DBNull.Value);
        command.Parameters.AddWithValue("$contentReference", (object?)evidence.ContentReference ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static EvidenceRecord ReadEvidence(SqliteDataReader reader)
        => EvidenceRecord.Create(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            ParseTimestamp(reader.GetString(5)),
            ReadNullableTimestamp(reader, 6),
            reader.GetString(7),
            reader.GetInt32(8),
            reader.GetString(9),
            Enum.Parse<RetentionState>(reader.GetString(10)),
            Enum.Parse<SensitivityClass>(reader.GetString(11)),
            ProducerRef.Create(reader.GetString(12), reader.GetString(13)),
            reader.GetInt32(14),
            ReadNullableString(reader, 15),
            ReadNullableString(reader, 16));

    private static async Task InsertEventAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ObservationEvent observationEvent,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO observation_events(
                event_id, owner_principal_id, event_type, occurred_at, observed_at,
                actor_refs_json, object_refs_json, evidence_refs_json, attributes_json,
                producer_id, producer_version, schema_version)
            VALUES (
                $id, $owner, $eventType, $occurredAt, $observedAt,
                $actorRefs, $objectRefs, $evidenceRefs, $attributes,
                $producerId, $producerVersion, $schemaVersion);
            """;
        command.Parameters.AddWithValue("$id", observationEvent.EventId);
        command.Parameters.AddWithValue("$owner", observationEvent.OwnerPrincipalId);
        command.Parameters.AddWithValue("$eventType", observationEvent.EventType);
        command.Parameters.AddWithValue("$occurredAt", FormatTimestamp(observationEvent.OccurredAt));
        command.Parameters.AddWithValue("$observedAt", FormatTimestamp(observationEvent.ObservedAt));
        command.Parameters.AddWithValue("$actorRefs", Serialize(observationEvent.ActorRefs));
        command.Parameters.AddWithValue("$objectRefs", Serialize(observationEvent.ObjectRefs));
        command.Parameters.AddWithValue("$evidenceRefs", Serialize(observationEvent.EvidenceRefs));
        command.Parameters.AddWithValue("$attributes", Serialize(observationEvent.Attributes));
        command.Parameters.AddWithValue("$producerId", observationEvent.Producer.Id);
        command.Parameters.AddWithValue("$producerVersion", observationEvent.Producer.Version);
        command.Parameters.AddWithValue("$schemaVersion", observationEvent.SchemaVersion);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ObservationEvent ReadEvent(SqliteDataReader reader)
        => ObservationEvent.Create(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            ParseTimestamp(reader.GetString(3)),
            ParseTimestamp(reader.GetString(4)),
            DeserializeReferences(reader.GetString(5)),
            DeserializeReferences(reader.GetString(6)),
            DeserializeReferences(reader.GetString(7)),
            DeserializeAttributes(reader.GetString(8)),
            ProducerRef.Create(reader.GetString(9), reader.GetString(10)),
            reader.GetInt32(11));

    private static async Task UpsertAssertionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AssertionRecord assertion,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO assertions(
                assertion_id, owner_principal_id, subject_key, predicate, value,
                assertion_type, epistemic_status, confidence, valid_from, valid_to,
                created_at, superseded_at, evidence_refs_json, lineage_refs_json,
                promotion_reason, producer_id, producer_version, schema_version)
            VALUES (
                $id, $owner, $subjectKey, $predicate, $value,
                $assertionType, $status, $confidence, $validFrom, $validTo,
                $createdAt, $supersededAt, $evidenceRefs, $lineageRefs,
                $promotionReason, $producerId, $producerVersion, $schemaVersion)
            ON CONFLICT(owner_principal_id, assertion_id) DO UPDATE SET
                value = excluded.value,
                assertion_type = excluded.assertion_type,
                epistemic_status = excluded.epistemic_status,
                confidence = excluded.confidence,
                valid_to = excluded.valid_to,
                superseded_at = excluded.superseded_at,
                evidence_refs_json = excluded.evidence_refs_json,
                lineage_refs_json = excluded.lineage_refs_json,
                promotion_reason = excluded.promotion_reason,
                producer_id = excluded.producer_id,
                producer_version = excluded.producer_version,
                schema_version = excluded.schema_version;
            """;
        command.Parameters.AddWithValue("$id", assertion.AssertionId);
        command.Parameters.AddWithValue("$owner", assertion.OwnerPrincipalId);
        command.Parameters.AddWithValue("$subjectKey", assertion.SubjectKey);
        command.Parameters.AddWithValue("$predicate", assertion.Predicate);
        command.Parameters.AddWithValue("$value", assertion.Value);
        command.Parameters.AddWithValue("$assertionType", assertion.AssertionType.ToString());
        command.Parameters.AddWithValue("$status", assertion.EpistemicStatus.ToString());
        command.Parameters.AddWithValue("$confidence", assertion.Confidence.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$validFrom", FormatTimestamp(assertion.ValidFrom));
        command.Parameters.AddWithValue("$validTo", assertion.ValidTo is null
            ? DBNull.Value
            : FormatTimestamp(assertion.ValidTo.Value));
        command.Parameters.AddWithValue("$createdAt", FormatTimestamp(assertion.CreatedAt));
        command.Parameters.AddWithValue("$supersededAt", assertion.SupersededAt is null
            ? DBNull.Value
            : FormatTimestamp(assertion.SupersededAt.Value));
        command.Parameters.AddWithValue("$evidenceRefs", Serialize(assertion.EvidenceRefs));
        command.Parameters.AddWithValue("$lineageRefs", Serialize(assertion.LineageRefs));
        command.Parameters.AddWithValue("$promotionReason", (object?)assertion.PromotionReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$producerId", assertion.Producer.Id);
        command.Parameters.AddWithValue("$producerVersion", assertion.Producer.Version);
        command.Parameters.AddWithValue("$schemaVersion", assertion.SchemaVersion);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static AssertionRecord ReadAssertion(SqliteDataReader reader)
        => AssertionRecord.Create(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            Enum.Parse<AssertionType>(reader.GetString(5)),
            Enum.Parse<EpistemicStatus>(reader.GetString(6)),
            decimal.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
            ParseTimestamp(reader.GetString(8)),
            ReadNullableTimestamp(reader, 9),
            ParseTimestamp(reader.GetString(10)),
            ReadNullableTimestamp(reader, 11),
            DeserializeReferences(reader.GetString(12)),
            DeserializeReferences(reader.GetString(13)),
            ReadNullableString(reader, 14),
            ProducerRef.Create(reader.GetString(15), reader.GetString(16)),
            reader.GetInt32(17));

    private static async Task<IReadOnlyList<AssertionRecord>> ReadAssertionsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<AssertionRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(ReadAssertion(reader));
        }

        return records.AsReadOnly();
    }
}