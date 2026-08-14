using Microsoft.Data.Sqlite;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Tessera.Core.Kernel;
using Tessera.Core.Product;
using Xunit;

namespace Tessera.Persistence.Sqlite.Tests;

public sealed class SqliteMigrationTests
{
    [Fact]
    public async Task Empty_store_bootstraps_and_repeatably_applies_v1_through_v19()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();

        await store.InitializeAsync();
        await store.InitializeAsync();

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19], await store.GetAppliedMigrationVersionsAsync());
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

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19], await restarted.GetAppliedMigrationVersionsAsync());
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

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19], await restarted.GetAppliedMigrationVersionsAsync());
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

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19], await restarted.GetAppliedMigrationVersionsAsync());
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

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19], await restarted.GetAppliedMigrationVersionsAsync());
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
    public async Task Populated_v18_store_upgrades_to_v19_without_rewriting_existing_data()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync(18);
        var owner = PrincipalRef.Create(
            "https://issuer.example", "tenant", "v17-owner", "v17-owner", DateTimeOffset.UtcNow);
        await store.AddAsync(owner);
        var evidence = KernelTestData.Evidence(owner.PrincipalId, "v17-evidence", "v17-payload");
        await store.AddAsync(owner.PrincipalId, evidence);

        var restarted = database.CreateStore();
        await restarted.InitializeAsync();

        Assert.Equal(19, (await restarted.GetAppliedMigrationVersionsAsync())[^1]);
        Assert.NotNull(await ((IEvidenceRepository)restarted).GetAsync(owner.PrincipalId, evidence.EvidenceId));
        var tables = await ReadTableNamesAsync(database.Path);
        Assert.Contains("remote_hosts", tables);
        Assert.Contains("host_work_leases", tables);
    }

    [Fact]
    public async Task V19_closed_domains_and_single_active_rows_are_enforced_by_sql()
    {
        using var database = new TemporaryDatabase();
        var store = database.CreateStore();
        await store.InitializeAsync();
        var owner = PrincipalRef.Create(
            "https://issuer.example", "tenant", "sql-owner", "sql-owner", DateTimeOffset.UtcNow);
        await store.AddAsync(owner);
        var now = new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);
        await store.AddJobAsync(new(
            owner.PrincipalId, "job-sql", "SQL Job", "Inspect repository", "ACTIVE", "READY", null,
            new JobSchedule("once", now, null, "UTC", null), null, "{}", [],
            [(RemoteHostValidation.SupportedCapabilityId, RemoteHostValidation.SupportedCapabilityVersion)],
            [], now, now, 1));
        Assert.NotNull(await store.CreateManualRunAsync(owner.PrincipalId, "job-sql", "run-sql", 1, now));
        Assert.NotNull(await store.CreateManualRunAsync(owner.PrincipalId, "job-sql", "run-sql-invalid", 1, now.AddSeconds(1)));
        Assert.Equal(1, await store.AcquireRunLeaseAsync(
            owner.PrincipalId, "run-sql", "sql-worker", now, TimeSpan.FromMinutes(10)));
        Assert.Equal(1, await store.AcquireRunLeaseAsync(
            owner.PrincipalId, "run-sql-invalid", "sql-worker", now, TimeSpan.FromMinutes(10)));

        await using var connection = new SqliteConnection(
            $"Data Source={database.Path};Foreign Keys=True;Pooling=False");
        await connection.OpenAsync();
        var ownerId = owner.PrincipalId.Replace("'", "''", StringComparison.Ordinal);
        var canonicalJwk = CanonicalJwk();
        await ExecuteAsync(connection, $$"""
            INSERT INTO remote_hosts(owner_principal_id,host_id,display_name,platform,architecture,
                lifecycle,connection_status,public_key_jwk,key_version,protection,agent_version,
                protocol_version,capability_catalog_version,last_accepted_sequence,last_seen_at,
                paired_at,revoked_at,version)
            VALUES('{{ownerId}}','host-sql','SQL Mac','macOS','arm64','ONLINE','ONLINE','{{canonicalJwk}}',1,
                'KEYCHAIN_THIS_DEVICE_ONLY','1.0.0','1',1,0,NULL,'2026-08-14T00:00:00Z',NULL,1);
            INSERT INTO host_capability_advertisements(owner_principal_id,host_id,capability_id,
                capability_version,schema_hash,side_effect_class,advertised_at)
            VALUES('{{ownerId}}','host-sql','host.repo.identity','1','{{new string('a', 64)}}',
                'READ_ONLY','2026-08-14T00:00:00Z');
            INSERT INTO host_resources(owner_principal_id,host_id,resource_id,type,display_name,
                fingerprint,state,advertised_at,version)
            VALUES('{{ownerId}}','host-sql','repo-sql','REPOSITORY','Repo','{{new string('b', 64)}}',
                'AVAILABLE','2026-08-14T00:00:00Z',1);
            INSERT INTO host_capability_grants(owner_principal_id,host_id,capability_id,
                capability_version,granted_at,revoked_at,version)
            VALUES('{{ownerId}}','host-sql','host.repo.identity','1','2026-08-14T00:00:00Z',NULL,1);
            INSERT INTO host_resource_grants(owner_principal_id,host_id,resource_id,access_mode,
                granted_at,revoked_at,version)
            VALUES('{{ownerId}}','host-sql','repo-sql','READ_ONLY','2026-08-14T00:00:00Z',NULL,1);
            INSERT INTO host_work_leases(owner_principal_id,lease_id,run_id,job_id,host_id,
                scheduler_fence,attempt,profile_id,capability_id,capability_version,
                capability_grant_version,input_hash,state,issued_at,execute_until,
                acknowledged_at,completed_at,local_attempt_id,outcome,output_sha256,failure_code,version)
            VALUES('{{ownerId}}','lease-valid','run-sql','job-sql','host-sql',1,1,
                'host.repo.identity@1','host.repo.identity','1',1,'{{new string('a', 64)}}',
                'OFFERED','2026-08-14T00:00:00Z','2026-08-14T00:10:00Z',
                NULL,NULL,NULL,NULL,NULL,NULL,1);
            """);

        var invalidStatements = new[]
        {
            $"INSERT INTO host_pairings VALUES('{ownerId}','pairing-hex','{new string('g', 64)}','ISSUED',0,0,NULL,'2026-08-14T00:00:00Z','2026-08-14T00:05:00Z',NULL,NULL,NULL,1);",
            RemoteHostInsert(ownerId, "host-jwk", "macOS", "arm64", "KEYCHAIN_THIS_DEVICE_ONLY", "1", "{}"),
            RemoteHostInsert(ownerId, "host-platform", "Linux", "arm64", "KEYCHAIN_THIS_DEVICE_ONLY", "1", "{\"p\":1}"),
            RemoteHostInsert(ownerId, "host-arch", "macOS", "x64", "KEYCHAIN_THIS_DEVICE_ONLY", "1", "{\"p\":2}"),
            RemoteHostInsert(ownerId, "host-protection", "macOS", "arm64", "FILE", "1", "{\"p\":3}"),
            RemoteHostInsert(ownerId, "host-protocol", "macOS", "arm64", "KEYCHAIN_THIS_DEVICE_ONLY", "2", "{\"p\":4}"),
            $"INSERT INTO host_capability_advertisements VALUES('{ownerId}','host-sql','host.shell','1','{new string('a', 64)}','READ_ONLY','2026-08-14T00:00:00Z');",
            $"INSERT INTO host_capability_advertisements VALUES('{ownerId}','host-sql','host.repo.identity','2','{new string('a', 64)}','READ_ONLY','2026-08-14T00:00:00Z');",
            $"INSERT INTO host_capability_advertisements VALUES('{ownerId}','host-sql','host.repo.identity','1','{new string('g', 64)}','READ_ONLY','2026-08-14T00:00:00Z');",
            $"INSERT INTO host_capability_advertisements VALUES('{ownerId}','host-sql','host.repo.identity','1','{new string('a', 64)}','WRITE','2026-08-14T00:00:00Z');",
            $"INSERT INTO host_resources VALUES('{ownerId}','host-sql','repo-type','DIRECTORY','Repo','{new string('b', 64)}','AVAILABLE','2026-08-14T00:00:00Z',1);",
            $"INSERT INTO host_resources VALUES('{ownerId}','host-sql','repo-fingerprint','REPOSITORY','Repo','{new string('g', 64)}','AVAILABLE','2026-08-14T00:00:00Z',1);",
            $"INSERT INTO host_resources VALUES('{ownerId}','host-sql','repo-state','REPOSITORY','Repo','{new string('b', 64)}','MISSING','2026-08-14T00:00:00Z',1);",
            $"INSERT INTO host_resource_grants VALUES('{ownerId}','host-sql','repo-sql','WRITE','2026-08-14T00:00:00Z',NULL,2);",
            $"INSERT INTO host_accepted_messages VALUES('{ownerId}','host-sql','message-operation',1,'shell','-','{new string('c', 64)}',200,'{{}}','2026-08-14T00:00:00Z');",
            $"INSERT INTO host_accepted_messages VALUES('{ownerId}','host-sql','message-hash',2,'poll','-','{new string('z', 64)}',200,'{{}}','2026-08-14T00:00:00Z');",
            $"INSERT INTO host_capability_grants VALUES('{ownerId}','host-sql','host.repo.identity','1','2026-08-14T00:00:01Z',NULL,2);",
            $"INSERT INTO host_resource_grants VALUES('{ownerId}','host-sql','repo-sql','READ_ONLY','2026-08-14T00:00:01Z',NULL,2);",
            $"INSERT INTO job_execution_policies VALUES('{ownerId}','job-sql','EDGE',NULL,'[]','[]','NONE',1);",
            $"INSERT INTO job_execution_policies VALUES('{ownerId}','job-sql','HOST',NULL,'[{{\"capabilityId\":\"host.repo.identity\",\"capabilityVersion\":\"1\"}}]','[\"repo-sql\"]','NONE',1);",
            $"INSERT INTO job_execution_policies VALUES('{ownerId}','job-sql','HOST','host-sql','[{{\"capabilityId\":\"host.repo.identity\",\"capabilityVersion\":\"1\"}}]','[]','NONE',1);",
            $"INSERT INTO job_execution_policies VALUES('{ownerId}','job-sql','ANY_COMPATIBLE_HOST','host-sql','[{{\"capabilityId\":\"host.repo.identity\",\"capabilityVersion\":\"1\"}}]','[\"repo-sql\"]','NONE',1);",
            $"INSERT INTO job_execution_policies VALUES('{ownerId}','job-sql','ANY_COMPATIBLE_HOST',NULL,'[{{\"capabilityId\":\"host.repo.identity\",\"capabilityVersion\":\"1\"}}]','[]','NONE',1);",
            $"INSERT INTO job_execution_policies VALUES('{ownerId}','job-sql','ANY_COMPATIBLE_HOST',NULL,'[{{\"capabilityId\":\"host.shell\",\"capabilityVersion\":\"1\"}}]','[\"repo-sql\"]','NONE',1);",
            $"INSERT INTO job_execution_policies VALUES('{ownerId}','job-sql','SERVER',NULL,'[{{\"capabilityId\":\"host.repo.identity\",\"capabilityVersion\":\"1\"}}]','[]','NONE',1);",
            $"INSERT INTO job_run_blockers VALUES('{ownerId}','run-missing','WAITING_FOR_SHELL',NULL,NULL,NULL,NULL,'2026-08-14T00:00:00Z',NULL,1);",
            $"INSERT INTO host_work_leases VALUES('{ownerId}','lease-sql','run-sql-invalid','job-sql','host-sql',1,1,'host.repo.identity@1','host.repo.identity','1',1,'{new string('a', 64)}','PAUSED','2026-08-14T00:00:00Z','2026-08-14T00:10:00Z',NULL,NULL,NULL,NULL,NULL,NULL,1);",
            $"INSERT INTO host_lease_resources VALUES('{ownerId}','lease-valid','repo-sql',1,'WRITE','{new string('b', 64)}');",
            $"INSERT INTO host_lease_resources VALUES('{ownerId}','lease-valid','repo-sql',2,'READ_ONLY','{new string('b', 64)}');",
            $"INSERT INTO host_lease_resources VALUES('{ownerId}','lease-valid','repo-sql',1,'READ_ONLY','{new string('c', 64)}');",
            $"INSERT INTO actions(action_id,owner_principal_id,capability_id,capability_version,intent,payload_hash,target_scope,risk_class,policy_decision_ref,authorization_ref,state,idempotency_key,attempt_count,created_at,started_at,completed_at,provider_receipt,verification_state,failure,schema_version,version,account_id,plugin_id,plugin_version,target_hash,expires_at,execution_id,conversation_id,message_id,job_id,job_run_id,host_id,host_lease_id,host_resource_grant_hash) VALUES('action-sql','{ownerId}','calendar.update','1','intent','{new string('d', 64)}','calendar/x','external-write','policy',NULL,'PROPOSED','idempotency-sql',0,'2026-08-14T00:00:00Z',NULL,NULL,NULL,NULL,NULL,1,0,NULL,'plugin','1.0.0','{new string('e', 64)}','2026-08-14T00:10:00Z','execution-sql',NULL,NULL,NULL,NULL,'host-sql',NULL,NULL);",
            $"INSERT INTO actions(action_id,owner_principal_id,capability_id,capability_version,intent,payload_hash,target_scope,risk_class,policy_decision_ref,authorization_ref,state,idempotency_key,attempt_count,created_at,started_at,completed_at,provider_receipt,verification_state,failure,schema_version,version,account_id,plugin_id,plugin_version,target_hash,expires_at,execution_id,conversation_id,message_id,job_id,job_run_id,host_id,host_lease_id,host_resource_grant_hash) VALUES('action-null-hash','{ownerId}','calendar.update','1','intent','{new string('d', 64)}','calendar/x','external-write','policy',NULL,'PROPOSED','idempotency-null-hash',0,'2026-08-14T00:00:00Z',NULL,NULL,NULL,NULL,NULL,1,0,NULL,'plugin','1.0.0','{new string('e', 64)}','2026-08-14T00:10:00Z','execution-null-hash',NULL,NULL,'job-sql','run-sql','host-sql','lease-valid',NULL);",
        };

        foreach (var statement in invalidStatements)
        {
            var error = await Record.ExceptionAsync(() => ExecuteAsync(connection, statement));
            Assert.True(error is SqliteException, $"Expected SQLite to reject: {statement}");
        }
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
                "development_job_specs",
                "development_workspaces",
                "durable_execution_requests",
                "evidence",
                "execution_controls",
                "execution_events",
                "follow_up_operations",
                "follow_up_revisions",
                "follow_up_sources",
                "follow_up_timeline",
                "follow_ups",
                "host_accepted_messages",
                "host_capability_advertisements",
                "host_capability_grants",
                "host_lease_events",
                "host_lease_resources",
                "host_pairings",
                "host_resource_grants",
                "host_resources",
                "host_work_leases",
                "idempotency_receipts",
                "job_account_grants",
                "job_capability_grants",
                "job_execution_policies",
                "job_outputs",
                "job_run_blockers",
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
                "remote_hosts",
                "scheduler_leases",
                "schema_migrations",
                "workflow_checkpoints",
            ],
            tables);
        Assert.DoesNotContain(tables, table => table.Contains("audit", StringComparison.OrdinalIgnoreCase));

        var columns = await ReadColumnNamesAsync(database.Path, tables);
        Assert.Contains("connected_accounts.credential_ref", columns);
        Assert.Contains("credential_cleanup_receipts.credential_ref", columns);
        Assert.Contains("jobs.kind", columns);
        Assert.Contains("jobs.conversation_id", columns);
        Assert.Contains("actions.host_resource_grant_hash", columns);
        Assert.Contains("action_authorizations.host_resource_grant_hash", columns);
        Assert.Contains("host_pairings.claim_secret_hash", columns);
        Assert.Contains("remote_hosts.public_key_jwk", columns);
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
        var forbiddenHostColumns = new[]
        {
            "claim_secret",
            "private_key",
            "local_path",
            "command",
            "environment",
            "signature",
        };
        var hostColumns = columns.Where(column =>
            column.StartsWith("host_", StringComparison.Ordinal)
            || column.StartsWith("remote_hosts.", StringComparison.Ordinal));
        Assert.DoesNotContain(hostColumns, column => forbiddenHostColumns.Any(value =>
            column.Contains(value, StringComparison.OrdinalIgnoreCase)
            && !column.EndsWith("claim_secret_hash", StringComparison.Ordinal)));
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

    private static string RemoteHostInsert(
        string owner, string hostId, string platform, string architecture,
        string protection, string protocol, string publicKeyJson)
        => $$"""
            INSERT INTO remote_hosts(owner_principal_id,host_id,display_name,platform,architecture,
                lifecycle,connection_status,public_key_jwk,key_version,protection,agent_version,
                protocol_version,capability_catalog_version,last_accepted_sequence,last_seen_at,
                paired_at,revoked_at,version)
            VALUES('{{owner}}','{{hostId}}','SQL Mac','{{platform}}','{{architecture}}','OFFLINE',
                'OFFLINE','{{publicKeyJson}}',1,'{{protection}}','1.0.0','{{protocol}}',1,0,NULL,
                '2026-08-14T00:00:00Z',NULL,1);
            """;

    private static string CanonicalJwk()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(false);
        static string Encode(byte[] value)
            => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return RemoteHostValidation.NormalizeP256PublicJwk(
            $$"""{"kty":"EC","crv":"P-256","x":"{{Encode(parameters.Q.X!)}}","y":"{{Encode(parameters.Q.Y!)}}"}""")
            .CanonicalJson;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
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