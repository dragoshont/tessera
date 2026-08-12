using System.Globalization;
using Microsoft.Data.Sqlite;
using Tessera.Core.Product;

namespace Tessera.Persistence.Sqlite;

public sealed partial class SqliteKernelStore
{
    public async Task AddCredentialCleanupReceiptAsync(
        string ownerPrincipalId, string receiptId, string accountId, string credentialRef,
        string state, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO credential_cleanup_receipts(owner_principal_id,receipt_id,account_id,credential_ref,state,created_at,updated_at,version)
            VALUES($owner,$receipt,$account,$credential,$state,$now,$now,1);
            """;
        command.Parameters.AddWithValue("$owner", ownerPrincipalId); command.Parameters.AddWithValue("$receipt", receiptId);
        command.Parameters.AddWithValue("$account", accountId); command.Parameters.AddWithValue("$credential", credentialRef);
        command.Parameters.AddWithValue("$state", state); command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddOrphanCredentialCleanupReceiptAsync(
        string ownerPrincipalId, string receiptId, string accountId, string credentialRef,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO orphan_credential_cleanup_receipts(owner_principal_id,receipt_id,account_id,credential_ref,state,created_at,updated_at,version)
            VALUES($owner,$receipt,$account,$credential,'PENDING',$now,$now,1);
            """;
        command.Parameters.AddWithValue("$owner", ownerPrincipalId); command.Parameters.AddWithValue("$receipt", receiptId);
        command.Parameters.AddWithValue("$account", accountId); command.Parameters.AddWithValue("$credential", credentialRef);
        command.Parameters.AddWithValue("$now", FormatTimestamp(now));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(string Owner,string ReceiptId,string CredentialRef)>> ListPendingOrphanCleanupAsync(CancellationToken token=default)
    {await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.CommandText="SELECT owner_principal_id,receipt_id,credential_ref FROM orphan_credential_cleanup_receipts WHERE state='PENDING' ORDER BY created_at LIMIT 100;";var values=new List<(string,string,string)>();await using var reader=await command.ExecuteReaderAsync(token).ConfigureAwait(false);while(await reader.ReadAsync(token).ConfigureAwait(false))values.Add((reader.GetString(0),reader.GetString(1),reader.GetString(2)));return values.AsReadOnly();}

    public async Task CompleteOrphanCleanupAsync(string owner,string receiptId,DateTimeOffset now,CancellationToken token=default)
    {await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.CommandText="UPDATE orphan_credential_cleanup_receipts SET state='COMPLETED',updated_at=$now,version=version+1 WHERE owner_principal_id=$owner AND receipt_id=$id AND state='PENDING';";command.Parameters.AddWithValue("$owner",owner);command.Parameters.AddWithValue("$now",FormatTimestamp(now));command.Parameters.AddWithValue("$id",receiptId);await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);}

    public async Task AddConnectedAccountAsync(
        ConnectedAccount account,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        EnsureOwner(account.OwnerPrincipalId, account.OwnerPrincipalId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO connected_accounts(
                    owner_principal_id,account_id,provider_id,plugin_id,plugin_version,display_name,
                    identity_hint,lifecycle,credential_ref,health,last_successful_use,non_secret_config_json,
                    created_at,updated_at,version)
                VALUES($owner,$id,$provider,$plugin,$pluginVersion,$name,$identity,$lifecycle,$credential,
                    $health,$lastUse,$config,$created,$updated,$version);
                """;
            AddAccountParameters(command, account);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var permission in account.Permissions.Distinct(StringComparer.Ordinal))
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO account_permissions(owner_principal_id,account_id,permission) VALUES($owner,$id,$value);",
                account.OwnerPrincipalId, account.AccountId, permission, cancellationToken).ConfigureAwait(false);
        }

        foreach (var binding in account.CapabilityBindings.Distinct())
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO account_capability_bindings(
                    owner_principal_id,account_id,plugin_id,plugin_version,capability_id,capability_version)
                VALUES($owner,$id,$plugin,$pluginVersion,$capability,$capabilityVersion);
                """;
            command.Parameters.AddWithValue("$owner", account.OwnerPrincipalId);
            command.Parameters.AddWithValue("$id", account.AccountId);
            command.Parameters.AddWithValue("$plugin", binding.PluginId);
            command.Parameters.AddWithValue("$pluginVersion", binding.PluginVersion);
            command.Parameters.AddWithValue("$capability", binding.CapabilityId);
            command.Parameters.AddWithValue("$capabilityVersion", binding.CapabilityVersion);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ConnectedAccount?> GetConnectedAccountAsync(
        string ownerPrincipalId,
        string accountId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadAccountAsync(connection, ownerPrincipalId, accountId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConnectedAccount>> ListConnectedAccountsAsync(
        string ownerPrincipalId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT account_id FROM connected_accounts WHERE owner_principal_id=$owner ORDER BY updated_at DESC,account_id;";
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        var ids = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) ids.Add(reader.GetString(0));
        }

        var accounts = new List<ConnectedAccount>(ids.Count);
        foreach (var id in ids)
        {
            var account = await ReadAccountAsync(connection, ownerPrincipalId, id, cancellationToken).ConfigureAwait(false);
            if (account is not null) accounts.Add(account);
        }
        return accounts.AsReadOnly();
    }

    public async Task<IReadOnlyList<ConnectedAccount>> ListConnectedAccountsByPluginAsync(
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT owner_principal_id,account_id
            FROM connected_accounts
            WHERE plugin_id=$plugin
            ORDER BY owner_principal_id,account_id;
            """;
        command.Parameters.AddWithValue("$plugin", pluginId);
        var keys = new List<(string Owner, string Account)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                keys.Add((reader.GetString(0), reader.GetString(1)));
        var values = new List<ConnectedAccount>(keys.Count);
        foreach (var key in keys)
            if (await ReadAccountAsync(connection, key.Owner, key.Account, cancellationToken).ConfigureAwait(false) is { } account)
                values.Add(account);
        return values.AsReadOnly();
    }

    public async Task<IReadOnlyList<ConnectedAccount>> ListConnectedAccountsByProviderAsync(string providerId,CancellationToken token=default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.CommandText="SELECT owner_principal_id,account_id FROM connected_accounts WHERE provider_id=$provider AND lifecycle NOT IN ('DISABLED','REVOKED') ORDER BY owner_principal_id,account_id;";command.Parameters.AddWithValue("$provider",providerId);var ids=new List<(string Owner,string Account)>();await using(var reader=await command.ExecuteReaderAsync(token).ConfigureAwait(false)){while(await reader.ReadAsync(token).ConfigureAwait(false))ids.Add((reader.GetString(0),reader.GetString(1)));}var values=new List<ConnectedAccount>(ids.Count);foreach(var id in ids)if(await ReadAccountAsync(connection,id.Owner,id.Account,token).ConfigureAwait(false) is { } account)values.Add(account);return values.AsReadOnly();
    }

    public async Task<ConnectedAccount> SetConnectedAccountStateAsync(
        string ownerPrincipalId,
        string accountId,
        long expectedVersion,
        AccountLifecycle lifecycle,
        AccountHealth health,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE connected_accounts SET lifecycle=$lifecycle,health=$health,updated_at=$updated,version=version+1
            WHERE owner_principal_id=$owner AND account_id=$id AND version=$expected AND lifecycle <> 'REVOKED';
            """;
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        command.Parameters.AddWithValue("$id", accountId);
        command.Parameters.AddWithValue("$expected", expectedVersion);
        command.Parameters.AddWithValue("$lifecycle", ToDatabase(lifecycle));
        command.Parameters.AddWithValue("$health", ToDatabase(health));
        command.Parameters.AddWithValue("$updated", FormatTimestamp(DateTimeOffset.UtcNow));
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new ProductConcurrencyException("Account version or lifecycle changed before commit.");
        return await ReadAccountAsync(connection, ownerPrincipalId, accountId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Updated account is missing.");
    }

    public async Task<ConnectedAccount> SetConnectedAccountValidationAsync(
        string ownerPrincipalId,
        string accountId,
        long expectedVersion,
        AccountLifecycle lifecycle,
        AccountHealth health,
        string providerAccountId,
        string identityHint,
        IReadOnlyList<string> verifiedPermissions,
        IReadOnlyList<string> providerScopes,
        IReadOnlyList<AccountCapabilityBinding> verifiedCapabilities,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerAccountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(identityHint);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE connected_accounts
                SET provider_account_id=$providerAccountId,provider_scopes_json=$providerScopes,identity_hint=$identity,lifecycle=$lifecycle,
                    health=$health,last_successful_use=$lastUse,updated_at=$updated,version=version+1
                WHERE owner_principal_id=$owner AND account_id=$id AND version=$expected AND lifecycle<>'REVOKED';
                """;
            command.Parameters.AddWithValue("$providerAccountId", providerAccountId);
            command.Parameters.AddWithValue("$providerScopes", System.Text.Json.JsonSerializer.Serialize(providerScopes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));
            command.Parameters.AddWithValue("$identity", identityHint);
            command.Parameters.AddWithValue("$lifecycle", ToDatabase(lifecycle));
            command.Parameters.AddWithValue("$health", ToDatabase(health));
            command.Parameters.AddWithValue("$lastUse", FormatTimestamp(now));
            command.Parameters.AddWithValue("$updated", FormatTimestamp(now));
            command.Parameters.AddWithValue("$owner", ownerPrincipalId);
            command.Parameters.AddWithValue("$id", accountId);
            command.Parameters.AddWithValue("$expected", expectedVersion);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new ProductConcurrencyException("Account version or lifecycle changed before validation commit.");
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM account_permissions WHERE owner_principal_id=$owner AND account_id=$id;";
            delete.Parameters.AddWithValue("$owner", ownerPrincipalId);
            delete.Parameters.AddWithValue("$id", accountId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var permission in verifiedPermissions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO account_permissions(owner_principal_id,account_id,permission) VALUES($owner,$id,$value);",
                ownerPrincipalId, accountId, permission, cancellationToken).ConfigureAwait(false);
        }
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM account_capability_bindings WHERE owner_principal_id=$owner AND account_id=$id;";
            delete.Parameters.AddWithValue("$owner", ownerPrincipalId);delete.Parameters.AddWithValue("$id", accountId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach(var binding in verifiedCapabilities.Distinct())
        {
            await using var insert=connection.CreateCommand();insert.Transaction=transaction;insert.CommandText="INSERT INTO account_capability_bindings(owner_principal_id,account_id,plugin_id,plugin_version,capability_id,capability_version) VALUES($owner,$id,$plugin,$pluginVersion,$capability,$capabilityVersion);";
            insert.Parameters.AddWithValue("$owner",ownerPrincipalId);insert.Parameters.AddWithValue("$id",accountId);insert.Parameters.AddWithValue("$plugin",binding.PluginId);insert.Parameters.AddWithValue("$pluginVersion",binding.PluginVersion);insert.Parameters.AddWithValue("$capability",binding.CapabilityId);insert.Parameters.AddWithValue("$capabilityVersion",binding.CapabilityVersion);await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await GetConnectedAccountAsync(ownerPrincipalId, accountId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Validated account is missing.");
    }

    public async Task<ConnectedAccount> RevokeConnectedAccountWithCleanupAsync(string owner,string accountId,long expectedVersion,string receiptId,string credentialRef,DateTimeOffset now,CancellationToken token=default)
    {
        await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var transaction=(SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await using(var update=connection.CreateCommand()){update.Transaction=transaction;update.CommandText="""
            UPDATE connected_accounts SET lifecycle='REVOKED',health='UNKNOWN',updated_at=$now,version=version+1
            WHERE owner_principal_id=$owner AND account_id=$account AND version=$version
              AND lifecycle<>'REVOKED' AND credential_ref=$credential;
            """;update.Parameters.AddWithValue("$owner",owner);update.Parameters.AddWithValue("$account",accountId);update.Parameters.AddWithValue("$version",expectedVersion);update.Parameters.AddWithValue("$credential",credentialRef);update.Parameters.AddWithValue("$now",FormatTimestamp(now));if(await update.ExecuteNonQueryAsync(token).ConfigureAwait(false)!=1)throw new ProductConcurrencyException("Account version, lifecycle, or credential reference changed before revocation.");}
        await using(var receipt=connection.CreateCommand()){receipt.Transaction=transaction;receipt.CommandText="""
            INSERT INTO orphan_credential_cleanup_receipts(owner_principal_id,receipt_id,account_id,credential_ref,state,created_at,updated_at,version)
            VALUES($owner,$receipt,$account,$credential,'PENDING',$now,$now,1);
            """;receipt.Parameters.AddWithValue("$owner",owner);receipt.Parameters.AddWithValue("$receipt",receiptId);receipt.Parameters.AddWithValue("$account",accountId);receipt.Parameters.AddWithValue("$credential",credentialRef);receipt.Parameters.AddWithValue("$now",FormatTimestamp(now));await receipt.ExecuteNonQueryAsync(token).ConfigureAwait(false);}
        await transaction.CommitAsync(token).ConfigureAwait(false);return await GetConnectedAccountAsync(owner,accountId,token).ConfigureAwait(false)??throw new InvalidDataException("Revoked account is missing.");
    }

    public async Task AddPluginInstallationAsync(PluginInstallation plugin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO plugin_installations(owner_principal_id,plugin_id,plugin_version,name,publisher,
                package_hash,manifest_json,configuration_json,enabled,installed_at,updated_at,version)
            VALUES($owner,$id,$pluginVersion,$name,$publisher,$hash,$manifest,$config,$enabled,$installed,$updated,$version);
            """;
        command.Parameters.AddWithValue("$owner", plugin.OwnerPrincipalId);
        command.Parameters.AddWithValue("$id", plugin.PluginId);
        command.Parameters.AddWithValue("$pluginVersion", plugin.PluginVersion);
        command.Parameters.AddWithValue("$name", plugin.Name);
        command.Parameters.AddWithValue("$publisher", plugin.Publisher);
        command.Parameters.AddWithValue("$hash", plugin.PackageHash);
        command.Parameters.AddWithValue("$manifest", plugin.ManifestJson);
        command.Parameters.AddWithValue("$config", plugin.ConfigurationJson);
        command.Parameters.AddWithValue("$enabled", plugin.Enabled);
        command.Parameters.AddWithValue("$installed", FormatTimestamp(plugin.InstalledAt));
        command.Parameters.AddWithValue("$updated", FormatTimestamp(plugin.UpdatedAt));
        command.Parameters.AddWithValue("$version", plugin.Version);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddModelProfileAsync(ModelProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO model_profiles(owner_principal_id,profile_id,account_id,adapter_kind,endpoint,model,
                context_limit,supports_streaming,supports_tools,enabled,created_at,updated_at,version)
            VALUES($owner,$id,$account,$adapter,$endpoint,$model,$limit,$streaming,$tools,$enabled,$created,$updated,$version);
            """;
        command.Parameters.AddWithValue("$owner", profile.OwnerPrincipalId);
        command.Parameters.AddWithValue("$id", profile.ProfileId);
        command.Parameters.AddWithValue("$account", profile.AccountId);
        command.Parameters.AddWithValue("$adapter", profile.AdapterKind);
        command.Parameters.AddWithValue("$endpoint", profile.Endpoint);
        command.Parameters.AddWithValue("$model", profile.Model);
        command.Parameters.AddWithValue("$limit", profile.ContextLimit);
        command.Parameters.AddWithValue("$streaming", profile.SupportsStreaming);
        command.Parameters.AddWithValue("$tools", profile.SupportsTools);
        command.Parameters.AddWithValue("$enabled", profile.Enabled);
        command.Parameters.AddWithValue("$created", FormatTimestamp(profile.CreatedAt));
        command.Parameters.AddWithValue("$updated", FormatTimestamp(profile.UpdatedAt));
        command.Parameters.AddWithValue("$version", profile.Version);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ModelProfile?> GetModelProfileAsync(string owner,string profileId,CancellationToken token=default)
    {
        await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.CommandText="SELECT account_id,adapter_kind,endpoint,model,context_limit,supports_streaming,supports_tools,enabled,created_at,updated_at,version FROM model_profiles WHERE owner_principal_id=$owner AND profile_id=$id;";command.Parameters.AddWithValue("$owner",owner);command.Parameters.AddWithValue("$id",profileId);await using var reader=await command.ExecuteReaderAsync(token).ConfigureAwait(false);if(!await reader.ReadAsync(token).ConfigureAwait(false))return null;return new(owner,profileId,reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetInt32(4),reader.GetBoolean(5),reader.GetBoolean(6),reader.GetBoolean(7),ParseTimestamp(reader.GetString(8)),ParseTimestamp(reader.GetString(9)),reader.GetInt64(10));
    }

    public async Task<IReadOnlyList<ModelProfile>> ListModelProfilesAsync(string owner,CancellationToken token=default)
    {
        await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.CommandText="SELECT profile_id FROM model_profiles WHERE owner_principal_id=$owner ORDER BY profile_id;";command.Parameters.AddWithValue("$owner",owner);var ids=new List<string>();await using(var reader=await command.ExecuteReaderAsync(token).ConfigureAwait(false)){while(await reader.ReadAsync(token).ConfigureAwait(false))ids.Add(reader.GetString(0));}var values=new List<ModelProfile>();foreach(var id in ids){var profile=await GetModelProfileAsync(owner,id,token).ConfigureAwait(false);if(profile is not null)values.Add(profile);}return values.AsReadOnly();
    }

    public async Task<IReadOnlyList<PluginInstallation>> ListPluginInstallationsAsync(string owner,CancellationToken token=default)
    {
        await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.CommandText="SELECT plugin_id,plugin_version,name,publisher,package_hash,manifest_json,configuration_json,enabled,installed_at,updated_at,version FROM plugin_installations WHERE owner_principal_id=$owner AND removed=0 ORDER BY name,plugin_id,plugin_version;";command.Parameters.AddWithValue("$owner",owner);var values=new List<PluginInstallation>();await using var reader=await command.ExecuteReaderAsync(token).ConfigureAwait(false);while(await reader.ReadAsync(token).ConfigureAwait(false))values.Add(new(owner,reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetString(6),reader.GetBoolean(7),ParseTimestamp(reader.GetString(8)),ParseTimestamp(reader.GetString(9)),reader.GetInt64(10)));return values.AsReadOnly();
    }

    public async Task<bool> SetPluginEnabledAsync(string owner,string pluginId,string pluginVersion,long expectedVersion,bool enabled,CancellationToken token=default)
    {var installation=await GetPluginInstallationAsync(owner,pluginId,pluginVersion,token).ConfigureAwait(false);if(installation is null)return false;var capabilities=ReadManifestCapabilities(installation.ManifestJson);await using var connection=await OpenConnectionAsync(token).ConfigureAwait(false);await using var transaction=(SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);await using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText="UPDATE plugin_installations SET enabled=$enabled,updated_at=$now,version=version+1 WHERE owner_principal_id=$owner AND plugin_id=$id AND plugin_version=$pluginVersion AND version=$expected AND removed=0;";command.Parameters.AddWithValue("$enabled",enabled);command.Parameters.AddWithValue("$now",FormatTimestamp(DateTimeOffset.UtcNow));command.Parameters.AddWithValue("$owner",owner);command.Parameters.AddWithValue("$id",pluginId);command.Parameters.AddWithValue("$pluginVersion",pluginVersion);command.Parameters.AddWithValue("$expected",expectedVersion);if(await command.ExecuteNonQueryAsync(token).ConfigureAwait(false)!=1)return false;foreach(var capability in capabilities){await using var jobs=connection.CreateCommand();jobs.Transaction=transaction;jobs.CommandText="UPDATE jobs SET health=$health,updated_at=$now,version=version+1 WHERE owner_principal_id=$owner AND desired_state<>'CANCELED' AND EXISTS(SELECT 1 FROM job_capability_grants grant_row WHERE grant_row.owner_principal_id=jobs.owner_principal_id AND grant_row.job_id=jobs.job_id AND grant_row.capability_id=$capability AND grant_row.capability_version=$capabilityVersion);";jobs.Parameters.AddWithValue("$health",enabled?"READY":"BLOCKED");jobs.Parameters.AddWithValue("$now",FormatTimestamp(DateTimeOffset.UtcNow));jobs.Parameters.AddWithValue("$owner",owner);jobs.Parameters.AddWithValue("$capability",capability.Id);jobs.Parameters.AddWithValue("$capabilityVersion",capability.Version);await jobs.ExecuteNonQueryAsync(token).ConfigureAwait(false);}await transaction.CommitAsync(token).ConfigureAwait(false);return true;}

    private static async Task<ConnectedAccount?> ReadAccountAsync(SqliteConnection connection, string owner, string id, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
                 SELECT provider_id,plugin_id,plugin_version,display_name,identity_hint,lifecycle,credential_ref,
                       health,last_successful_use,non_secret_config_json,created_at,updated_at,version,provider_account_id,provider_scopes_json
            FROM connected_accounts WHERE owner_principal_id=$owner AND account_id=$id;
            """;
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var account = new ConnectedAccount(owner, id, reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), ReadNullableString(reader, 4), ParseEnum<AccountLifecycle>(reader.GetString(5)),
            reader.GetString(6), ParseEnum<AccountHealth>(reader.GetString(7)), ReadNullableTimestamp(reader, 8),
            reader.GetString(9), [], [], ParseTimestamp(reader.GetString(10)), ParseTimestamp(reader.GetString(11)), reader.GetInt64(12));
        var providerAccountId = ReadNullableString(reader, 13);
        var providerScopes = System.Text.Json.JsonSerializer.Deserialize<string[]>(reader.GetString(14)) ?? [];
        await reader.DisposeAsync().ConfigureAwait(false);
        var permissions = await ReadStringsAsync(connection,
            "SELECT permission FROM account_permissions WHERE owner_principal_id=$owner AND account_id=$id ORDER BY permission;", owner, id, token).ConfigureAwait(false);
        var bindings = await ReadBindingsAsync(connection, owner, id, token).ConfigureAwait(false);
        account = account with
        {
            Permissions = permissions,
            CapabilityBindings = bindings,
            ProviderAccountId = providerAccountId,
            ProviderScopes = providerScopes,
        };
        ConnectedAccountCredentialRef.Validate(account, owner);
        return account;
    }

    private static async Task<IReadOnlyList<string>> ReadStringsAsync(SqliteConnection connection, string sql, string owner, string id, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        command.Parameters.AddWithValue("$owner", owner); command.Parameters.AddWithValue("$id", id);
        var values = new List<string>(); await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false)) values.Add(reader.GetString(0));
        return values.AsReadOnly();
    }

    private static async Task<IReadOnlyList<AccountCapabilityBinding>> ReadBindingsAsync(SqliteConnection connection, string owner, string id, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT plugin_id,plugin_version,capability_id,capability_version FROM account_capability_bindings WHERE owner_principal_id=$owner AND account_id=$id ORDER BY plugin_id,plugin_version,capability_id,capability_version;";
        command.Parameters.AddWithValue("$owner", owner); command.Parameters.AddWithValue("$id", id);
        var values = new List<AccountCapabilityBinding>(); await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false)) values.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3)));
        return values.AsReadOnly();
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, string owner, string id, string value, CancellationToken token)
    {
        await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        command.Parameters.AddWithValue("$owner", owner); command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static void AddAccountParameters(SqliteCommand command, ConnectedAccount account)
    {
        command.Parameters.AddWithValue("$owner", account.OwnerPrincipalId); command.Parameters.AddWithValue("$id", account.AccountId);
        command.Parameters.AddWithValue("$provider", account.ProviderId); command.Parameters.AddWithValue("$plugin", account.PluginId);
        command.Parameters.AddWithValue("$pluginVersion", account.PluginVersion); command.Parameters.AddWithValue("$name", account.DisplayName);
        command.Parameters.AddWithValue("$identity", (object?)account.IdentityHint ?? DBNull.Value); command.Parameters.AddWithValue("$lifecycle", ToDatabase(account.Lifecycle));
        command.Parameters.AddWithValue("$credential", account.CredentialRef); command.Parameters.AddWithValue("$health", ToDatabase(account.Health));
        command.Parameters.AddWithValue("$lastUse", account.LastSuccessfulUse is null ? DBNull.Value : FormatTimestamp(account.LastSuccessfulUse.Value));
        command.Parameters.AddWithValue("$config", account.NonSecretConfigJson); command.Parameters.AddWithValue("$created", FormatTimestamp(account.CreatedAt));
        command.Parameters.AddWithValue("$updated", FormatTimestamp(account.UpdatedAt)); command.Parameters.AddWithValue("$version", account.Version);
    }

    private static string ToDatabase<T>(T value) where T : struct, Enum
        => string.Concat(value.ToString().Select((character, index) => char.IsUpper(character) && index > 0 ? "_" + character : character.ToString())).ToUpperInvariant();

    private static T ParseEnum<T>(string value) where T : struct, Enum
        => Enum.Parse<T>(value.Replace("_", string.Empty, StringComparison.Ordinal), true);
}