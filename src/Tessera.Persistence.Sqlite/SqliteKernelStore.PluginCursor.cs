using Microsoft.Data.Sqlite;
using Tessera.Core.Kernel;

namespace Tessera.Persistence.Sqlite;

public sealed record PluginCursorRow(
    string OwnerPrincipalId,
    string AccountId,
    string PluginId,
    string StateKey,
    string Cursor,
    string MetadataJson,
    DateTimeOffset UpdatedAt,
    long Version);

public sealed partial class SqliteKernelStore
{
    public async Task<PluginCursorRow?> GetPluginCursorAsync(
        string ownerPrincipalId,
        string accountId,
        string pluginId,
        string stateKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT cursor,metadata_json,updated_at,version
            FROM plugin_cursor_states
            WHERE owner_principal_id=$owner AND account_id=$account
              AND plugin_id=$plugin AND state_key=$key;
            """;
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$plugin", pluginId);
        command.Parameters.AddWithValue("$key", stateKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(
                ownerPrincipalId,
                accountId,
                pluginId,
                stateKey,
                reader.GetString(0),
                reader.GetString(1),
                ParseTimestamp(reader.GetString(2)),
                reader.GetInt64(3))
            : null;
    }

    public async Task CommitPluginCursorAsync(
        PluginCursorRow state,
        IReadOnlyList<EvidenceRecord> evidence,
        IReadOnlyList<ObservationEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var item in evidence) await InsertEvidenceAsync(connection, transaction, item, cancellationToken).ConfigureAwait(false);
        foreach (var item in events) await InsertEventAsync(connection, transaction, item, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO plugin_cursor_states(
                owner_principal_id,account_id,plugin_id,state_key,cursor,metadata_json,updated_at,version)
            VALUES($owner,$account,$plugin,$key,$cursor,$metadata,$updated,1)
            ON CONFLICT(owner_principal_id,account_id,plugin_id,state_key) DO UPDATE SET
                cursor=excluded.cursor,metadata_json=excluded.metadata_json,
                updated_at=excluded.updated_at,version=plugin_cursor_states.version+1;
            """;
        command.Parameters.AddWithValue("$owner", state.OwnerPrincipalId);
        command.Parameters.AddWithValue("$account", state.AccountId);
        command.Parameters.AddWithValue("$plugin", state.PluginId);
        command.Parameters.AddWithValue("$key", state.StateKey);
        command.Parameters.AddWithValue("$cursor", state.Cursor);
        command.Parameters.AddWithValue("$metadata", state.MetadataJson);
        command.Parameters.AddWithValue("$updated", FormatTimestamp(state.UpdatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}