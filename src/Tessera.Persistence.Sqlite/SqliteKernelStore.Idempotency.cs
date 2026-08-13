using Microsoft.Data.Sqlite;
using Tessera.Core.Product;

namespace Tessera.Persistence.Sqlite;

public sealed record ProductIdempotencyReceipt(
    string OwnerPrincipalId,
    string RouteFamily,
    string IdempotencyKey,
    string RequestHash,
    int ResponseStatus,
    string ResponseBodyJson,
    string ResourceType,
    string ResourceId,
    DateTimeOffset CreatedAt);

public sealed record PluginInstallCommitResult(
    ProductIdempotencyReceipt Receipt,
    bool Created);

public sealed partial class SqliteKernelStore
{
    public async Task<ProductIdempotencyReceipt?> GetIdempotencyReceiptAsync(
        string owner,
        string routeFamily,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT request_hash,response_status,response_body_json,resource_type,resource_id,created_at
            FROM idempotency_receipts
            WHERE owner_principal_id=$owner AND route_family=$route AND idempotency_key=$key;
            """;
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$route", routeFamily);
        command.Parameters.AddWithValue("$key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(
                owner,
                routeFamily,
                idempotencyKey,
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                ParseTimestamp(reader.GetString(5)))
            : null;
    }

    public async Task<ProductIdempotencyReceipt?> FindIdempotencyReceiptByResourceAsync(
        string owner, string routeFamily, string resourceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT idempotency_key,request_hash,response_status,response_body_json,resource_type,created_at
            FROM idempotency_receipts
            WHERE owner_principal_id=$owner AND route_family=$route AND resource_id=$resource
            ORDER BY created_at LIMIT 1;
            """;
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$route", routeFamily);
        command.Parameters.AddWithValue("$resource", resourceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(owner, routeFamily, reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                reader.GetString(3), reader.GetString(4), resourceId, ParseTimestamp(reader.GetString(5)))
            : null;
    }

    public async Task<PluginInstallCommitResult> CommitPluginInstallWithReceiptAsync(
        PluginInstallation installation,
        ProductIdempotencyReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var prior = await ReadReceiptAsync(
                connection,
                transaction,
                receipt.OwnerPrincipalId,
                receipt.RouteFamily,
                receipt.IdempotencyKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (prior is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(prior, false);
        }

        string? existingHash = null;
        var removed = false;
        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = """
                SELECT package_hash,removed
                FROM plugin_installations
                WHERE owner_principal_id=$owner AND plugin_id=$id AND plugin_version=$version;
                """;
            existing.Parameters.AddWithValue("$owner", installation.OwnerPrincipalId);
            existing.Parameters.AddWithValue("$id", installation.PluginId);
            existing.Parameters.AddWithValue("$version", installation.PluginVersion);
            await using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                existingHash = reader.GetString(0);
                removed = reader.GetBoolean(1);
            }
        }

        if (removed) throw new InvalidOperationException("package_previously_removed");
        if (existingHash is not null
            && !string.Equals(existingHash, installation.PackageHash, StringComparison.Ordinal))
            throw new InvalidOperationException("package_hash_conflict");
        if (existingHash is null)
        {
            await using var install = connection.CreateCommand();
            install.Transaction = transaction;
            install.CommandText = """
                INSERT INTO plugin_installations(owner_principal_id,plugin_id,plugin_version,name,publisher,
                    package_hash,manifest_json,configuration_json,enabled,installed_at,updated_at,version)
                VALUES($owner,$id,$pluginVersion,$name,$publisher,$hash,$manifest,$config,$enabled,$installed,$updated,$version);
                """;
            install.Parameters.AddWithValue("$owner", installation.OwnerPrincipalId);
            install.Parameters.AddWithValue("$id", installation.PluginId);
            install.Parameters.AddWithValue("$pluginVersion", installation.PluginVersion);
            install.Parameters.AddWithValue("$name", installation.Name);
            install.Parameters.AddWithValue("$publisher", installation.Publisher);
            install.Parameters.AddWithValue("$hash", installation.PackageHash);
            install.Parameters.AddWithValue("$manifest", installation.ManifestJson);
            install.Parameters.AddWithValue("$config", installation.ConfigurationJson);
            install.Parameters.AddWithValue("$enabled", installation.Enabled);
            install.Parameters.AddWithValue("$installed", FormatTimestamp(installation.InstalledAt));
            install.Parameters.AddWithValue("$updated", FormatTimestamp(installation.UpdatedAt));
            install.Parameters.AddWithValue("$version", installation.Version);
            await install.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO idempotency_receipts(
                owner_principal_id,route_family,idempotency_key,request_hash,response_status,
                response_body_json,resource_type,resource_id,created_at)
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
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(receipt, true);
    }

    private static async Task<ProductIdempotencyReceipt?> ReadReceiptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string owner,
        string routeFamily,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT request_hash,response_status,response_body_json,resource_type,resource_id,created_at
            FROM idempotency_receipts
            WHERE owner_principal_id=$owner AND route_family=$route AND idempotency_key=$key;
            """;
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$route", routeFamily);
        command.Parameters.AddWithValue("$key", idempotencyKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(
                owner,
                routeFamily,
                idempotencyKey,
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                ParseTimestamp(reader.GetString(5)))
            : null;
    }
}
