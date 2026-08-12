namespace Tessera.Persistence.Sqlite;

internal sealed record KernelMigration(int Version, string Sql);

internal static class KernelMigrations
{
    public const int LatestVersion = 15;

    public static IReadOnlyList<KernelMigration> All { get; } =
    [
        new KernelMigration(1, Migration1),
        new KernelMigration(2, Migration2),
        new KernelMigration(3, Migration3),
        new KernelMigration(4, Migration4),
        new KernelMigration(5, Migration5),
        new KernelMigration(6, Migration6),
        new KernelMigration(7, Migration7),
        new KernelMigration(8, Migration8),
        new KernelMigration(9, Migration9),
        new KernelMigration(10, Migration10),
        new KernelMigration(11, Migration11),
        new KernelMigration(12, Migration12),
        new KernelMigration(13, Migration13),
        new KernelMigration(14, Migration14),
        new KernelMigration(15, Migration15),
    ];

    private const string Migration15 = """
        ALTER TABLE capability_calls ADD COLUMN external_server_id TEXT NULL;
        ALTER TABLE capability_calls ADD COLUMN external_server_name TEXT NULL;
        ALTER TABLE capability_calls ADD COLUMN external_server_version TEXT NULL;
        ALTER TABLE capability_calls ADD COLUMN external_tool_name TEXT NULL;
        """;

    private const string Migration14 = """
        CREATE TABLE plugin_cursor_states (
            owner_principal_id TEXT NOT NULL,
            account_id TEXT NOT NULL,
            plugin_id TEXT NOT NULL,
            state_key TEXT NOT NULL,
            cursor TEXT NOT NULL,
            metadata_json TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            version INTEGER NOT NULL,
            PRIMARY KEY (owner_principal_id, account_id, plugin_id, state_key),
            FOREIGN KEY (owner_principal_id, account_id)
                REFERENCES connected_accounts(owner_principal_id, account_id)
        );

        INSERT INTO plugin_cursor_states(
            owner_principal_id,account_id,plugin_id,state_key,cursor,metadata_json,updated_at,version)
        SELECT owner_principal_id,account_id,'gmail','history',history_id,
               '{"initialLookbackDays":' || initial_lookback_days || '}',last_synced_at,version
        FROM gmail_sync_state;

         DROP TABLE gmail_sync_state;
        """;

    private const string Migration1 = """
        CREATE TABLE principals (
            principal_id TEXT PRIMARY KEY,
            issuer TEXT NOT NULL,
            tenant TEXT NOT NULL,
            subject TEXT NOT NULL,
            display_hint TEXT NULL,
            created_at TEXT NOT NULL,
            UNIQUE (issuer, tenant, subject)
        );

        CREATE TABLE evidence (
            evidence_id TEXT NOT NULL,
            owner_principal_id TEXT NOT NULL,
            source_type TEXT NOT NULL,
            source_native_id TEXT NOT NULL,
            source_locator TEXT NOT NULL,
            observed_at TEXT NOT NULL,
            source_timestamp TEXT NULL,
            hash_algorithm TEXT NOT NULL,
            hash_version INTEGER NOT NULL,
            content_hash TEXT NOT NULL,
            retention_state TEXT NOT NULL,
            sensitivity TEXT NOT NULL,
            producer_id TEXT NOT NULL,
            producer_version TEXT NOT NULL,
            schema_version INTEGER NOT NULL,
            bounded_excerpt TEXT NULL,
            content_reference TEXT NULL,
            PRIMARY KEY (owner_principal_id, evidence_id),
            FOREIGN KEY (owner_principal_id) REFERENCES principals(principal_id)
        );

        CREATE TABLE observation_events (
            event_id TEXT NOT NULL,
            owner_principal_id TEXT NOT NULL,
            event_type TEXT NOT NULL,
            occurred_at TEXT NOT NULL,
            observed_at TEXT NOT NULL,
            actor_refs_json TEXT NOT NULL,
            object_refs_json TEXT NOT NULL,
            evidence_refs_json TEXT NOT NULL,
            attributes_json TEXT NOT NULL,
            producer_id TEXT NOT NULL,
            producer_version TEXT NOT NULL,
            schema_version INTEGER NOT NULL,
            PRIMARY KEY (owner_principal_id, event_id),
            FOREIGN KEY (owner_principal_id) REFERENCES principals(principal_id)
        );

        CREATE TABLE assertions (
            assertion_id TEXT NOT NULL,
            owner_principal_id TEXT NOT NULL,
            subject_key TEXT NOT NULL,
            predicate TEXT NOT NULL,
            value TEXT NOT NULL,
            assertion_type TEXT NOT NULL,
            epistemic_status TEXT NOT NULL,
            confidence TEXT NOT NULL,
            valid_from TEXT NOT NULL,
            valid_to TEXT NULL,
            created_at TEXT NOT NULL,
            superseded_at TEXT NULL,
            evidence_refs_json TEXT NOT NULL,
            lineage_refs_json TEXT NOT NULL,
            promotion_reason TEXT NULL,
            producer_id TEXT NOT NULL,
            producer_version TEXT NOT NULL,
            schema_version INTEGER NOT NULL,
            PRIMARY KEY (owner_principal_id, assertion_id),
            FOREIGN KEY (owner_principal_id) REFERENCES principals(principal_id)
        );

        CREATE INDEX ix_evidence_owner_observed
            ON evidence(owner_principal_id, observed_at DESC);
        CREATE INDEX ix_evidence_owner_hash
            ON evidence(owner_principal_id, content_hash);
        CREATE INDEX ix_events_owner_occurred
            ON observation_events(owner_principal_id, occurred_at DESC);
        CREATE INDEX ix_assertions_owner_status_valid
            ON assertions(owner_principal_id, epistemic_status, valid_from DESC);
        CREATE INDEX ix_assertions_owner_key
            ON assertions(owner_principal_id, subject_key, predicate, created_at DESC);
        CREATE UNIQUE INDEX ux_assertions_owner_current_key
            ON assertions(owner_principal_id, subject_key, predicate)
            WHERE epistemic_status = 'Current';
        """;

    private const string Migration2 = """
        CREATE TABLE actions (
            action_id TEXT NOT NULL,
            owner_principal_id TEXT NOT NULL,
            capability_id TEXT NOT NULL,
            capability_version TEXT NOT NULL,
            intent TEXT NOT NULL,
            payload_hash TEXT NOT NULL,
            target_scope TEXT NOT NULL,
            risk_class TEXT NOT NULL,
            policy_decision_ref TEXT NOT NULL,
            authorization_ref TEXT NULL,
            state TEXT NOT NULL,
            idempotency_key TEXT NOT NULL,
            attempt_count INTEGER NOT NULL,
            created_at TEXT NOT NULL,
            started_at TEXT NULL,
            completed_at TEXT NULL,
            provider_receipt TEXT NULL,
            verification_state TEXT NULL,
            failure TEXT NULL,
            schema_version INTEGER NOT NULL,
            version INTEGER NOT NULL,
            PRIMARY KEY (owner_principal_id, action_id),
            FOREIGN KEY (owner_principal_id) REFERENCES principals(principal_id),
            UNIQUE (owner_principal_id, idempotency_key)
        );

        CREATE TABLE action_authorizations (
            authorization_id TEXT NOT NULL,
            owner_principal_id TEXT NOT NULL,
            capability_id TEXT NOT NULL,
            capability_version TEXT NOT NULL,
            action_id TEXT NOT NULL,
            payload_hash TEXT NOT NULL,
            target_scope TEXT NOT NULL,
            issued_at TEXT NOT NULL,
            expires_at TEXT NOT NULL,
            consumed_at TEXT NULL,
            PRIMARY KEY (owner_principal_id, authorization_id),
            FOREIGN KEY (owner_principal_id, action_id)
                REFERENCES actions(owner_principal_id, action_id)
        );

        CREATE TABLE workflow_checkpoints (
            workflow_id TEXT NOT NULL,
            owner_principal_id TEXT NOT NULL,
            workflow_type TEXT NOT NULL,
            state TEXT NOT NULL,
            current_step TEXT NOT NULL,
            input_refs_json TEXT NOT NULL,
            output_refs_json TEXT NOT NULL,
            wake_condition TEXT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            version INTEGER NOT NULL,
            PRIMARY KEY (owner_principal_id, workflow_id),
            FOREIGN KEY (owner_principal_id) REFERENCES principals(principal_id)
        );

        CREATE INDEX ix_actions_owner_state_created
            ON actions(owner_principal_id, state, created_at DESC);
        CREATE INDEX ix_actions_owner_idempotency
            ON actions(owner_principal_id, idempotency_key);
        CREATE INDEX ix_authorizations_owner_expiry
            ON action_authorizations(owner_principal_id, expires_at);
        CREATE INDEX ix_workflows_owner_updated
            ON workflow_checkpoints(owner_principal_id, updated_at DESC);
        """;

    private const string Migration3 = """
        CREATE TABLE follow_ups (
            owner_principal_id TEXT NOT NULL,
            follow_up_id TEXT NOT NULL,
            status TEXT NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            version INTEGER NOT NULL,
            PRIMARY KEY (owner_principal_id, follow_up_id),
            FOREIGN KEY (owner_principal_id) REFERENCES principals(principal_id)
        );

        CREATE TABLE follow_up_revisions (
            owner_principal_id TEXT NOT NULL,
            follow_up_id TEXT NOT NULL,
            revision_id TEXT NOT NULL,
            field TEXT NOT NULL,
            value TEXT NOT NULL,
            state TEXT NOT NULL,
            evidence_refs_json TEXT NOT NULL,
            source_timestamp TEXT NOT NULL,
            parser_version TEXT NOT NULL,
            confidence TEXT NOT NULL,
            correction_evidence_ref TEXT NULL,
            lineage_revision_refs_json TEXT NOT NULL,
            created_at TEXT NOT NULL,
            PRIMARY KEY (owner_principal_id, revision_id),
            FOREIGN KEY (owner_principal_id, follow_up_id)
                REFERENCES follow_ups(owner_principal_id, follow_up_id)
        );

        CREATE TABLE follow_up_timeline (
            owner_principal_id TEXT NOT NULL,
            follow_up_id TEXT NOT NULL,
            sequence INTEGER NOT NULL,
            kind TEXT NOT NULL,
            field TEXT NULL,
            summary TEXT NOT NULL,
            evidence_ref TEXT NOT NULL,
            source_timestamp TEXT NOT NULL,
            recorded_at TEXT NOT NULL,
            PRIMARY KEY (owner_principal_id, follow_up_id, sequence),
            FOREIGN KEY (owner_principal_id, follow_up_id)
                REFERENCES follow_ups(owner_principal_id, follow_up_id)
        );

        CREATE TABLE follow_up_sources (
            owner_principal_id TEXT NOT NULL,
            source_type TEXT NOT NULL,
            source_native_id TEXT NOT NULL,
            follow_up_id TEXT NOT NULL,
            result_version INTEGER NOT NULL,
            PRIMARY KEY (owner_principal_id, source_type, source_native_id),
            FOREIGN KEY (owner_principal_id, follow_up_id)
                REFERENCES follow_ups(owner_principal_id, follow_up_id)
        );

        CREATE TABLE follow_up_operations (
            owner_principal_id TEXT NOT NULL,
            operation_id TEXT NOT NULL,
            request_hash TEXT NOT NULL,
            follow_up_id TEXT NOT NULL,
            result_version INTEGER NOT NULL,
            PRIMARY KEY (owner_principal_id, operation_id),
            FOREIGN KEY (owner_principal_id, follow_up_id)
                REFERENCES follow_ups(owner_principal_id, follow_up_id)
        );

        CREATE INDEX ix_follow_ups_owner_status_updated
            ON follow_ups(owner_principal_id, status, updated_at DESC, follow_up_id);
        CREATE INDEX ix_follow_up_revisions_owner_follow_up_field
            ON follow_up_revisions(owner_principal_id, follow_up_id, field, created_at);
        CREATE INDEX ix_follow_up_timeline_owner_follow_up_recorded
            ON follow_up_timeline(owner_principal_id, follow_up_id, recorded_at, sequence);
        """;

    private const string Migration4 = """
        ALTER TABLE follow_up_sources
            ADD COLUMN source_payload_hash TEXT NOT NULL DEFAULT '';
        """;

    private const string Migration5 = """
        CREATE TABLE connected_accounts (
            owner_principal_id TEXT NOT NULL,
            account_id TEXT NOT NULL,
            provider_id TEXT NOT NULL,
            plugin_id TEXT NOT NULL,
            plugin_version TEXT NOT NULL,
            display_name TEXT NOT NULL,
            identity_hint TEXT NULL,
            lifecycle TEXT NOT NULL CHECK (lifecycle IN (
                'CONNECTING', 'CONNECTED', 'DEGRADED', 'AUTH_REQUIRED',
                'ERROR', 'DISABLED', 'REVOKED')),
            credential_ref TEXT NOT NULL,
            health TEXT NOT NULL CHECK (health IN (
                'UNKNOWN', 'HEALTHY', 'DEGRADED', 'AUTH_REQUIRED', 'ERROR')),
            last_successful_use TEXT NULL,
            non_secret_config_json TEXT NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            version INTEGER NOT NULL CHECK (version >= 1),
            PRIMARY KEY (owner_principal_id, account_id),
            FOREIGN KEY (owner_principal_id) REFERENCES principals(principal_id),
            UNIQUE (owner_principal_id, credential_ref)
        );

        CREATE TABLE account_permissions (
            owner_principal_id TEXT NOT NULL,
            account_id TEXT NOT NULL,
            permission TEXT NOT NULL,
            PRIMARY KEY (owner_principal_id, account_id, permission),
            FOREIGN KEY (owner_principal_id, account_id)
                REFERENCES connected_accounts(owner_principal_id, account_id)
        );

        CREATE TABLE account_capability_bindings (
            owner_principal_id TEXT NOT NULL,
            account_id TEXT NOT NULL,
            plugin_id TEXT NOT NULL,
            plugin_version TEXT NOT NULL,
            capability_id TEXT NOT NULL,
            capability_version TEXT NOT NULL,
            PRIMARY KEY (
                owner_principal_id, account_id, plugin_id, plugin_version,
                capability_id, capability_version),
            FOREIGN KEY (owner_principal_id, account_id)
                REFERENCES connected_accounts(owner_principal_id, account_id)
        );

        CREATE TABLE credential_cleanup_receipts (
            owner_principal_id TEXT NOT NULL,
            receipt_id TEXT NOT NULL,
            account_id TEXT NOT NULL,
            credential_ref TEXT NOT NULL,
            state TEXT NOT NULL CHECK (state IN ('PENDING', 'COMPLETED', 'FAILED')),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            version INTEGER NOT NULL CHECK (version >= 1),
            PRIMARY KEY (owner_principal_id, receipt_id),
            FOREIGN KEY (owner_principal_id, account_id)
                REFERENCES connected_accounts(owner_principal_id, account_id)
        );

        CREATE TABLE plugin_installations (
            owner_principal_id TEXT NOT NULL,
            plugin_id TEXT NOT NULL,
            plugin_version TEXT NOT NULL,
            name TEXT NOT NULL,
            publisher TEXT NOT NULL,
            package_hash TEXT NOT NULL,
            manifest_json TEXT NOT NULL,
            configuration_json TEXT NOT NULL,
            enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
            installed_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            version INTEGER NOT NULL CHECK (version >= 1),
            PRIMARY KEY (owner_principal_id, plugin_id, plugin_version),
            FOREIGN KEY (owner_principal_id) REFERENCES principals(principal_id)
        );

        CREATE TABLE model_profiles (
            owner_principal_id TEXT NOT NULL,
            profile_id TEXT NOT NULL,
            account_id TEXT NOT NULL,
            adapter_kind TEXT NOT NULL CHECK (adapter_kind IN (
                'openai-compatible-remote', 'openai-compatible-local')),
            endpoint TEXT NOT NULL,
            model TEXT NOT NULL,
            context_limit INTEGER NOT NULL CHECK (context_limit > 0),
            supports_streaming INTEGER NOT NULL CHECK (supports_streaming IN (0, 1)),
            supports_tools INTEGER NOT NULL CHECK (supports_tools IN (0, 1)),
            enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            version INTEGER NOT NULL CHECK (version >= 1),
            PRIMARY KEY (owner_principal_id, profile_id),
            FOREIGN KEY (owner_principal_id, account_id)
                REFERENCES connected_accounts(owner_principal_id, account_id)
        );

        CREATE TABLE idempotency_receipts (
            owner_principal_id TEXT NOT NULL,
            route_family TEXT NOT NULL,
            idempotency_key TEXT NOT NULL,
            request_hash TEXT NOT NULL,
            response_status INTEGER NOT NULL CHECK (response_status BETWEEN 100 AND 599),
            response_body_json TEXT NOT NULL,
            resource_type TEXT NOT NULL,
            resource_id TEXT NOT NULL,
            created_at TEXT NOT NULL,
            PRIMARY KEY (owner_principal_id, route_family, idempotency_key),
            FOREIGN KEY (owner_principal_id) REFERENCES principals(principal_id)
        );

        CREATE INDEX ix_accounts_owner_lifecycle_updated
            ON connected_accounts(owner_principal_id, lifecycle, updated_at DESC, account_id);
        CREATE INDEX ix_accounts_owner_provider
            ON connected_accounts(owner_principal_id, provider_id, account_id);
        CREATE INDEX ix_cleanup_owner_state_updated
            ON credential_cleanup_receipts(owner_principal_id, state, updated_at, receipt_id);
        CREATE INDEX ix_plugins_owner_enabled_name
            ON plugin_installations(owner_principal_id, enabled, name, plugin_id, plugin_version);
        CREATE INDEX ix_model_profiles_owner_enabled
            ON model_profiles(owner_principal_id, enabled, profile_id);
        CREATE INDEX ix_idempotency_owner_resource
            ON idempotency_receipts(owner_principal_id, resource_type, resource_id);
        """;

    private const string Migration6 = """
        CREATE TABLE conversations (
            owner_principal_id TEXT NOT NULL, conversation_id TEXT NOT NULL,
            title TEXT NOT NULL, state TEXT NOT NULL CHECK (state IN ('ACTIVE','ARCHIVED','DELETED')),
            model_profile_id TEXT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
            version INTEGER NOT NULL CHECK (version >= 1),
            PRIMARY KEY (owner_principal_id, conversation_id),
            FOREIGN KEY (owner_principal_id) REFERENCES principals(principal_id),
            FOREIGN KEY (owner_principal_id, model_profile_id) REFERENCES model_profiles(owner_principal_id, profile_id)
        );
        CREATE TABLE messages (
            owner_principal_id TEXT NOT NULL, message_id TEXT NOT NULL, conversation_id TEXT NOT NULL,
            role TEXT NOT NULL CHECK (role IN ('USER','ASSISTANT','SYSTEM_EVENT','CAPABILITY')),
            status TEXT NOT NULL CHECK (status IN ('PERSISTED','RUNNING','COMPLETED','FAILED','STOPPED')),
            retry_of TEXT NULL, created_at TEXT NOT NULL, completed_at TEXT NULL,
            version INTEGER NOT NULL CHECK (version >= 1),
            PRIMARY KEY (owner_principal_id, message_id),
            FOREIGN KEY (owner_principal_id, conversation_id) REFERENCES conversations(owner_principal_id, conversation_id),
            FOREIGN KEY (owner_principal_id, retry_of) REFERENCES messages(owner_principal_id, message_id)
        );
        CREATE TABLE message_parts (
            owner_principal_id TEXT NOT NULL, part_id TEXT NOT NULL, message_id TEXT NOT NULL,
            sequence INTEGER NOT NULL CHECK (sequence >= 0),
            kind TEXT NOT NULL CHECK (kind IN ('TEXT','STATUS','CAPABILITY_CALL','CAPABILITY_RESULT','ACTION','EVIDENCE','FAILURE')),
            text TEXT NULL, capability_call_id TEXT NULL, capability_result_id TEXT NULL,
            action_id TEXT NULL, evidence_refs_json TEXT NOT NULL, error_code TEXT NULL,
            PRIMARY KEY (owner_principal_id, part_id),
            FOREIGN KEY (owner_principal_id, message_id) REFERENCES messages(owner_principal_id, message_id),
            UNIQUE (owner_principal_id, message_id, sequence),
            CHECK ((kind IN ('TEXT','STATUS') AND text IS NOT NULL) OR
                   (kind = 'CAPABILITY_CALL' AND capability_call_id IS NOT NULL) OR
                   (kind = 'CAPABILITY_RESULT' AND capability_result_id IS NOT NULL) OR
                   (kind = 'ACTION' AND action_id IS NOT NULL) OR
                   (kind = 'EVIDENCE' AND evidence_refs_json <> '[]') OR
                   (kind = 'FAILURE' AND error_code IS NOT NULL))
        );
        CREATE TABLE capability_calls (
            owner_principal_id TEXT NOT NULL, call_id TEXT NOT NULL, execution_id TEXT NOT NULL,
            conversation_id TEXT NULL, message_id TEXT NULL, job_id TEXT NULL, job_run_id TEXT NULL,
            plugin_id TEXT NOT NULL, plugin_version TEXT NOT NULL,
            capability_id TEXT NOT NULL, capability_version TEXT NOT NULL, account_id TEXT NULL,
            input_json TEXT NOT NULL, input_hash TEXT NOT NULL,
            state TEXT NOT NULL CHECK (state IN ('REQUESTED','RUNNING','SUCCEEDED','FAILED','BLOCKED')),
            created_at TEXT NOT NULL, completed_at TEXT NULL, error_code TEXT NULL,
            version INTEGER NOT NULL CHECK (version >= 1),
            PRIMARY KEY (owner_principal_id, call_id),
            FOREIGN KEY (owner_principal_id) REFERENCES principals(principal_id),
            FOREIGN KEY (owner_principal_id, account_id) REFERENCES connected_accounts(owner_principal_id, account_id)
        );
        CREATE TABLE capability_results (
            owner_principal_id TEXT NOT NULL, result_id TEXT NOT NULL, call_id TEXT NOT NULL,
            summary TEXT NOT NULL, data_json TEXT NOT NULL, evidence_refs_json TEXT NOT NULL,
            truncated INTEGER NOT NULL CHECK (truncated IN (0,1)), created_at TEXT NOT NULL,
            PRIMARY KEY (owner_principal_id, result_id),
            FOREIGN KEY (owner_principal_id, call_id) REFERENCES capability_calls(owner_principal_id, call_id)
        );
        CREATE TABLE context_snapshot_refs (
            owner_principal_id TEXT NOT NULL, snapshot_ref TEXT NOT NULL, execution_id TEXT NOT NULL,
            source_refs_json TEXT NOT NULL, omitted_count INTEGER NOT NULL CHECK (omitted_count >= 0),
            sensitivity_classes_json TEXT NOT NULL, captured_at TEXT NOT NULL,
            PRIMARY KEY (owner_principal_id, snapshot_ref),
            FOREIGN KEY (owner_principal_id) REFERENCES principals(principal_id)
        );
        CREATE TABLE execution_events (
            owner_principal_id TEXT NOT NULL, event_id TEXT NOT NULL, execution_id TEXT NOT NULL,
            sequence INTEGER NOT NULL CHECK (sequence > 0),
            event_type TEXT NOT NULL CHECK (event_type IN ('status','text','capability_requested','approval_required','capability_result','failure','completed')),
            occurred_at TEXT NOT NULL, message_id TEXT NULL, capability_call_id TEXT NULL,
            action_id TEXT NULL, data_json TEXT NOT NULL,
            PRIMARY KEY (owner_principal_id, event_id),
            FOREIGN KEY (owner_principal_id) REFERENCES principals(principal_id),
            UNIQUE (owner_principal_id, execution_id, sequence)
        );

        ALTER TABLE actions ADD COLUMN account_id TEXT NULL;
        ALTER TABLE actions ADD COLUMN plugin_id TEXT NULL;
        ALTER TABLE actions ADD COLUMN plugin_version TEXT NULL;
        ALTER TABLE actions ADD COLUMN target_hash TEXT NULL;
        ALTER TABLE actions ADD COLUMN expires_at TEXT NULL;
        ALTER TABLE actions ADD COLUMN execution_id TEXT NULL;
        ALTER TABLE actions ADD COLUMN conversation_id TEXT NULL;
        ALTER TABLE actions ADD COLUMN message_id TEXT NULL;
        ALTER TABLE actions ADD COLUMN job_id TEXT NULL;
        ALTER TABLE actions ADD COLUMN job_run_id TEXT NULL;
        ALTER TABLE action_authorizations ADD COLUMN account_id TEXT NULL;
        ALTER TABLE action_authorizations ADD COLUMN plugin_id TEXT NULL;
        ALTER TABLE action_authorizations ADD COLUMN plugin_version TEXT NULL;
        ALTER TABLE action_authorizations ADD COLUMN target_hash TEXT NULL;
        ALTER TABLE action_authorizations ADD COLUMN execution_id TEXT NULL;

        CREATE INDEX ix_conversations_owner_state_updated ON conversations(owner_principal_id,state,updated_at DESC,conversation_id);
        CREATE INDEX ix_messages_owner_conversation_created ON messages(owner_principal_id,conversation_id,created_at,message_id);
        CREATE INDEX ix_calls_owner_execution_created ON capability_calls(owner_principal_id,execution_id,created_at,call_id);
        CREATE INDEX ix_events_owner_execution_sequence ON execution_events(owner_principal_id,execution_id,sequence);
        """;

    private const string Migration7 = """
        CREATE TABLE jobs (
            owner_principal_id TEXT NOT NULL, job_id TEXT NOT NULL, name TEXT NOT NULL,
            instruction TEXT NOT NULL, desired_state TEXT NOT NULL CHECK (desired_state IN ('DRAFT','ACTIVE','PAUSED','CANCELED')),
            health TEXT NOT NULL CHECK (health IN ('READY','DEGRADED','BLOCKED')),
            model_profile_id TEXT NULL, schedule_json TEXT NOT NULL, next_occurrence TEXT NULL,
            context_policy_json TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
            version INTEGER NOT NULL CHECK (version >= 1),
            PRIMARY KEY (owner_principal_id, job_id),
            FOREIGN KEY (owner_principal_id) REFERENCES principals(principal_id),
            FOREIGN KEY (owner_principal_id, model_profile_id) REFERENCES model_profiles(owner_principal_id, profile_id)
        );
        CREATE TABLE job_account_grants (
            owner_principal_id TEXT NOT NULL, job_id TEXT NOT NULL, account_id TEXT NOT NULL,
            PRIMARY KEY (owner_principal_id,job_id,account_id),
            FOREIGN KEY (owner_principal_id,job_id) REFERENCES jobs(owner_principal_id,job_id),
            FOREIGN KEY (owner_principal_id,account_id) REFERENCES connected_accounts(owner_principal_id,account_id)
        );
        CREATE TABLE job_capability_grants (
            owner_principal_id TEXT NOT NULL, job_id TEXT NOT NULL,
            capability_id TEXT NOT NULL, capability_version TEXT NOT NULL,
            PRIMARY KEY (owner_principal_id,job_id,capability_id,capability_version),
            FOREIGN KEY (owner_principal_id,job_id) REFERENCES jobs(owner_principal_id,job_id)
        );
        CREATE TABLE job_side_effect_grants (
            owner_principal_id TEXT NOT NULL, job_id TEXT NOT NULL, side_effect_class TEXT NOT NULL,
            PRIMARY KEY (owner_principal_id,job_id,side_effect_class),
            FOREIGN KEY (owner_principal_id,job_id) REFERENCES jobs(owner_principal_id,job_id)
        );
        CREATE TABLE job_runs (
            owner_principal_id TEXT NOT NULL, run_id TEXT NOT NULL, job_id TEXT NOT NULL,
            scheduled_for TEXT NOT NULL, state TEXT NOT NULL CHECK (state IN ('QUEUED','RUNNING','WAITING_FOR_APPROVAL','RECONCILIATION_REQUIRED','SUCCEEDED','FAILED','CANCELED')),
            started_at TEXT NULL, ended_at TEXT NULL, model_profile_id TEXT NULL,
            context_snapshot_ref TEXT NULL, error_code TEXT NULL,
            fence INTEGER NOT NULL DEFAULT 0 CHECK (fence >= 0), version INTEGER NOT NULL CHECK (version >= 1),
            PRIMARY KEY (owner_principal_id,run_id),
            FOREIGN KEY (owner_principal_id,job_id) REFERENCES jobs(owner_principal_id,job_id),
            UNIQUE (owner_principal_id,job_id,scheduled_for)
        );
        CREATE TABLE job_run_checkpoints (
            owner_principal_id TEXT NOT NULL, run_id TEXT NOT NULL, sequence INTEGER NOT NULL CHECK (sequence > 0),
            step TEXT NOT NULL, state_json TEXT NOT NULL, fence INTEGER NOT NULL CHECK (fence > 0), created_at TEXT NOT NULL,
            PRIMARY KEY (owner_principal_id,run_id,sequence),
            FOREIGN KEY (owner_principal_id,run_id) REFERENCES job_runs(owner_principal_id,run_id)
        );
        CREATE TABLE scheduler_leases (
            owner_principal_id TEXT NOT NULL, run_id TEXT NOT NULL, holder_id TEXT NOT NULL,
            acquired_at TEXT NOT NULL, expires_at TEXT NOT NULL, fence INTEGER NOT NULL CHECK (fence > 0),
            PRIMARY KEY (owner_principal_id,run_id),
            FOREIGN KEY (owner_principal_id,run_id) REFERENCES job_runs(owner_principal_id,run_id)
        );
        CREATE TABLE job_outputs (
            owner_principal_id TEXT NOT NULL, output_ref TEXT NOT NULL, run_id TEXT NOT NULL,
            kind TEXT NOT NULL, media_type TEXT NOT NULL, summary TEXT NOT NULL, text TEXT NULL,
            truncated INTEGER NOT NULL CHECK (truncated IN (0,1)), created_at TEXT NOT NULL,
            PRIMARY KEY (owner_principal_id,output_ref),
            FOREIGN KEY (owner_principal_id,run_id) REFERENCES job_runs(owner_principal_id,run_id)
        );
        CREATE TABLE product_settings (
            owner_principal_id TEXT NOT NULL, settings_id TEXT NOT NULL CHECK (settings_id = 'default'),
            default_chat_model_profile_id TEXT NULL, default_lightweight_model_profile_id TEXT NULL,
            timezone TEXT NOT NULL, approval_defaults_json TEXT NOT NULL, memory_controls_json TEXT NOT NULL,
            updated_at TEXT NOT NULL, version INTEGER NOT NULL CHECK (version >= 1),
            PRIMARY KEY (owner_principal_id,settings_id),
            FOREIGN KEY (owner_principal_id) REFERENCES principals(principal_id)
        );
        CREATE INDEX ix_jobs_owner_state_next ON jobs(owner_principal_id,desired_state,next_occurrence,job_id);
        CREATE INDEX ix_runs_owner_job_scheduled ON job_runs(owner_principal_id,job_id,scheduled_for DESC,run_id);
        CREATE INDEX ix_runs_owner_state_scheduled ON job_runs(owner_principal_id,state,scheduled_for,run_id);
        CREATE INDEX ix_leases_expiry ON scheduler_leases(expires_at,owner_principal_id,run_id);
        """;

    private const string Migration8 = """
        CREATE TABLE orphan_credential_cleanup_receipts (
            owner_principal_id TEXT NOT NULL,
            receipt_id TEXT NOT NULL,
            account_id TEXT NOT NULL,
            credential_ref TEXT NOT NULL,
            state TEXT NOT NULL CHECK (state IN ('PENDING', 'COMPLETED', 'FAILED')),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            version INTEGER NOT NULL CHECK (version >= 1),
            PRIMARY KEY (owner_principal_id, receipt_id),
            FOREIGN KEY (owner_principal_id) REFERENCES principals(principal_id)
        );
        CREATE INDEX ix_orphan_cleanup_owner_state_updated
            ON orphan_credential_cleanup_receipts(owner_principal_id,state,updated_at,receipt_id);
        """;

    private const string Migration9 = """
        ALTER TABLE plugin_installations ADD COLUMN removed INTEGER NOT NULL DEFAULT 0 CHECK (removed IN (0,1));
        CREATE TABLE durable_execution_requests (
            owner_principal_id TEXT NOT NULL,
            action_id TEXT NOT NULL,
            execution_id TEXT NOT NULL,
            capability_id TEXT NOT NULL,
            capability_version TEXT NOT NULL,
            plugin_id TEXT NOT NULL,
            plugin_version TEXT NOT NULL,
            account_id TEXT NULL,
            target_scope TEXT NOT NULL,
            target_hash TEXT NOT NULL,
            input_json TEXT NOT NULL,
            idempotency_key TEXT NOT NULL,
            conversation_id TEXT NULL,
            message_id TEXT NULL,
            job_id TEXT NULL,
            job_run_id TEXT NULL,
            created_at TEXT NOT NULL,
            PRIMARY KEY (owner_principal_id, action_id),
            FOREIGN KEY (owner_principal_id, action_id) REFERENCES actions(owner_principal_id, action_id)
        );
        CREATE INDEX ix_durable_requests_owner_job_run
            ON durable_execution_requests(owner_principal_id,job_run_id,action_id);
        CREATE TABLE execution_controls (
            owner_principal_id TEXT NOT NULL,
            execution_id TEXT NOT NULL,
            conversation_id TEXT NOT NULL,
            message_id TEXT NULL,
            state TEXT NOT NULL CHECK (state IN ('RUNNING','STOPPED','COMPLETED','FAILED')),
            updated_at TEXT NOT NULL,
            version INTEGER NOT NULL CHECK (version >= 1),
            PRIMARY KEY (owner_principal_id,execution_id),
            FOREIGN KEY (owner_principal_id,conversation_id) REFERENCES conversations(owner_principal_id,conversation_id)
        );
        CREATE INDEX ix_execution_controls_owner_conversation
            ON execution_controls(owner_principal_id,conversation_id,updated_at DESC,execution_id);
        """;

    private const string Migration10 = """
        ALTER TABLE execution_controls ADD COLUMN model_profile_id TEXT NULL;
        ALTER TABLE execution_controls ADD COLUMN idempotency_key TEXT NULL;
        CREATE UNIQUE INDEX ix_execution_controls_owner_idempotency
            ON execution_controls(owner_principal_id,conversation_id,idempotency_key)
            WHERE idempotency_key IS NOT NULL;
        """;

    private const string Migration11 = """
        CREATE TABLE conversation_account_grants (
            owner_principal_id TEXT NOT NULL, conversation_id TEXT NOT NULL, account_id TEXT NOT NULL,
            PRIMARY KEY(owner_principal_id,conversation_id,account_id),
            FOREIGN KEY(owner_principal_id,conversation_id) REFERENCES conversations(owner_principal_id,conversation_id),
            FOREIGN KEY(owner_principal_id,account_id) REFERENCES connected_accounts(owner_principal_id,account_id)
        );
        CREATE TABLE conversation_capability_grants (
            owner_principal_id TEXT NOT NULL, conversation_id TEXT NOT NULL,
            capability_id TEXT NOT NULL, capability_version TEXT NOT NULL,
            PRIMARY KEY(owner_principal_id,conversation_id,capability_id,capability_version),
            FOREIGN KEY(owner_principal_id,conversation_id) REFERENCES conversations(owner_principal_id,conversation_id)
        );
        """;

    private const string Migration12 = """
        ALTER TABLE connected_accounts ADD COLUMN provider_account_id TEXT NULL;
        ALTER TABLE connected_accounts ADD COLUMN provider_scopes_json TEXT NOT NULL DEFAULT '[]';
        CREATE INDEX ix_accounts_owner_provider_identity
            ON connected_accounts(owner_principal_id,provider_id,provider_account_id)
            WHERE provider_account_id IS NOT NULL;
        """;

    private const string Migration13 = """
        CREATE TABLE gmail_sync_state (
            owner_principal_id TEXT NOT NULL,
            account_id TEXT NOT NULL,
            history_id TEXT NOT NULL,
            initial_lookback_days INTEGER NOT NULL CHECK(initial_lookback_days BETWEEN 1 AND 90),
            last_synced_at TEXT NOT NULL,
            version INTEGER NOT NULL CHECK(version >= 1),
            PRIMARY KEY(owner_principal_id,account_id),
            FOREIGN KEY(owner_principal_id,account_id) REFERENCES connected_accounts(owner_principal_id,account_id)
        );
        """;
}