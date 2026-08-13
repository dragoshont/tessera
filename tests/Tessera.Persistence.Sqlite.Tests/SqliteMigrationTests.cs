using Microsoft.Data.Sqlite;
using System.Collections.ObjectModel;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Xunit;

namespace Tessera.Persistence.Sqlite.Tests;

public sealed class SqliteMigrationTests
{
    [Fact]
    public async Task Empty_store_bootstraps_and_repeatably_applies_v1_through_v16()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();

        await store.InitializeAsync();
        await store.InitializeAsync();

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16], await store.GetAppliedMigrationVersionsAsync());
    }

    [Fact]
    public async Task Prior_v1_fixture_migrates_through_v3()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync(1);
        Assert.Equal([1], await store.GetAppliedMigrationVersionsAsync());

        var restarted = database.CreateStore();
        await restarted.InitializeAsync();

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16], await restarted.GetAppliedMigrationVersionsAsync());
        Assert.Contains("actions", await ReadTableNamesAsync(database.Path));
        Assert.Contains("follow_ups", await ReadTableNamesAsync(database.Path));
    }

    [Fact]
    public async Task Prior_v2_fixture_migrates_additively_to_v3()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync(2);

        var restarted = database.CreateStore();
        await restarted.InitializeAsync();

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16], await restarted.GetAppliedMigrationVersionsAsync());
        Assert.Contains("follow_up_operations", await ReadTableNamesAsync(database.Path));
    }

    [Fact]
    public async Task Prior_v3_fixture_adds_source_payload_binding_in_v4()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync(3);

        var restarted = database.CreateStore();
        await restarted.InitializeAsync();

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16], await restarted.GetAppliedMigrationVersionsAsync());
        var columns = await ReadColumnNamesAsync(database.Path, ["follow_up_sources"]);
        Assert.Contains("follow_up_sources.source_payload_hash", columns);
    }

    [Fact]
    public async Task Prior_v4_fixture_adds_product_registry_in_v5()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync(4);

        var restarted = database.CreateStore();
        await restarted.InitializeAsync();

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16], await restarted.GetAppliedMigrationVersionsAsync());
        var tables = await ReadTableNamesAsync(database.Path);
        Assert.Contains("connected_accounts", tables);
        Assert.Contains("plugin_installations", tables);
        Assert.Contains("model_profiles", tables);
        Assert.Contains("idempotency_receipts", tables);
        Assert.Contains("conversations", tables);
        Assert.Contains("jobs", tables);
        Assert.Contains("scheduler_leases", tables);
        Assert.Contains("connected_accounts.provider_account_id",
            await ReadColumnNamesAsync(database.Path, ["connected_accounts"]));
        Assert.Contains("connected_accounts.provider_scopes_json",
            await ReadColumnNamesAsync(database.Path, ["connected_accounts"]));
    }

    [Fact]
    public async Task Prior_v13_Gmail_cursor_migrates_to_generic_plugin_state()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync(13);
        var now = DateTimeOffset.UtcNow;
        var owner = PrincipalRef.Create("https://issuer.example", "tenant", "subject", "owner", now);
        await store.AddAsync(owner);
        await store.AddConnectedAccountAsync(new(
            owner.PrincipalId,
            "mail-account",
            "mail",
            "mail-plugin",
            "1.0.0",
            "Mail",
            null,
            AccountLifecycle.Connected,
            ConnectedAccountCredentialRef.Create(owner.PrincipalId, "mail-account"),
            AccountHealth.Healthy,
            now,
            "{}",
            [],
            [],
            now,
            now,
            1));
        await using (var connection = new SqliteConnection($"Data Source={database.Path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO gmail_sync_state(
                    owner_principal_id,account_id,history_id,initial_lookback_days,last_synced_at,version)
                VALUES($owner,'mail-account','12345',30,$now,2);
                """;
            command.Parameters.AddWithValue("$owner", owner.PrincipalId);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        var restarted = database.CreateStore();
        await restarted.InitializeAsync();

        var cursor = await restarted.GetPluginCursorAsync(
            owner.PrincipalId,
            "mail-account",
            "gmail",
            "history");
        Assert.NotNull(cursor);
        Assert.Equal("12345", cursor.Cursor);
        Assert.Contains("30", cursor.MetadataJson, StringComparison.Ordinal);
        Assert.DoesNotContain("gmail_sync_state", await ReadTableNamesAsync(database.Path));
    }

    [Fact]
    public async Task Connections_enforce_foreign_keys_wal_and_busy_timeout()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();

        var settings = await store.GetConnectionSettingsAsync();

        Assert.True(settings.ForeignKeysEnabled);
        Assert.Equal("wal", settings.JournalMode, ignoreCase: true);
        Assert.Equal(5000, settings.BusyTimeoutMilliseconds);
    }

    [Fact]
    public async Task Configured_database_size_limit_sets_a_hard_page_ceiling()
    {
        using var database = new TemporaryDatabase();
        const long limit = 64L * 1024 * 1024;
        var store = new SqliteKernelStore(database.Path, limit);
        await store.InitializeAsync();

        var settings = await store.GetConnectionSettingsAsync();

        Assert.InRange(settings.MaxDatabaseBytes, limit - settings.PageSizeBytes, limit);
    }

    [Fact]
    public async Task Foreign_key_rejects_state_for_unknown_owner()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var evidence = KernelTestData.Evidence("unknown-owner", "evidence-1", "payload");

        await Assert.ThrowsAsync<SqliteException>(() => store.AddAsync("unknown-owner", evidence));
    }

    [Fact]
    public async Task Schema_contains_product_state_only()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();

        var tables = await ReadTableNamesAsync(database.Path);

        Assert.Equal(
            [
                "account_capability_bindings",
                "account_permissions",
                "action_authorizations",
                "actions",
                "assertions",
                "capability_calls",
                "capability_results",
                "connected_accounts",
                "context_snapshot_refs",
                "conversation_account_grants",
                "conversation_capability_grants",
                "conversations",
                "credential_cleanup_receipts",
                "durable_execution_requests",
                "evidence",
                "execution_controls",
                "execution_events",
                "follow_up_operations",
                "follow_up_revisions",
                "follow_up_sources",
                "follow_up_timeline",
                "follow_ups",
                "idempotency_receipts",
                "job_account_grants",
                "job_capability_grants",
                "job_outputs",
                "job_run_checkpoints",
                "job_runs",
                "job_side_effect_grants",
                "jobs",
                "message_parts",
                "messages",
                "model_profiles",
                "observation_events",
                "orphan_credential_cleanup_receipts",
                "plugin_cursor_states",
                "plugin_installations",
                "principals",
                "product_settings",
                "realtime_session_receipts",
                "realtime_session_tools",
                "realtime_tool_bindings",
                "realtime_turn_receipts",
                "scheduler_leases",
                "schema_migrations",
                "workflow_checkpoints",
            ],
            tables);
        Assert.DoesNotContain(tables, table => table.Contains("policy", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(tables, table => table.Contains("audit", StringComparison.OrdinalIgnoreCase));

        var columns = await ReadColumnNamesAsync(database.Path, tables);
        Assert.Contains("connected_accounts.credential_ref", columns);
        Assert.Contains("credential_cleanup_receipts.credential_ref", columns);
        var forbidden = new[]
        {
            "prompt",
            "structured_output",
            "model_output",
            "worker_output",
            "diagnostics",
            "oauth",
            "token",
            "password",
            "credential_value",
            "api_key",
        };
        Assert.DoesNotContain(columns, column => forbidden.Any(
            value => column.Contains(value, StringComparison.OrdinalIgnoreCase)));
    }

    private static async Task<ReadOnlyCollection<string>> ReadTableNamesAsync(string databasePath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_schema
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
            ORDER BY name;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names.AsReadOnly();
    }

    private static async Task<ReadOnlyCollection<string>> ReadColumnNamesAsync(
        string databasePath,
        IEnumerable<string> tables)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        await connection.OpenAsync();
        var names = new List<string>();
        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info('{table.Replace("'", "''", StringComparison.Ordinal)}');";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                names.Add($"{table}.{reader.GetString(1)}");
            }
        }

        return names.AsReadOnly();
    }
}