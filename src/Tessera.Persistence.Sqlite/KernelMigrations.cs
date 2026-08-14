namespace Tessera.Persistence.Sqlite;

internal sealed record KernelMigration(int Version, string Sql);

internal static class KernelMigrations
{
    public const int LatestVersion = 20;

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
        new KernelMigration(16, Migration16),
        new KernelMigration(17, Migration17),
        new KernelMigration(18, Migration18),
        new KernelMigration(19, Migration19),
        new KernelMigration(20, Migration20),
    ];

    private const string Migration20 = """
        ALTER TABLE host_accepted_messages RENAME TO host_accepted_messages_v19;

        CREATE TABLE host_accepted_messages (
            owner_principal_id TEXT NOT NULL,
            host_id TEXT NOT NULL,
            message_id TEXT NOT NULL,
            sequence INTEGER NOT NULL CHECK (sequence >= 1),
            operation TEXT NOT NULL CHECK (operation IN (
                'poll','lease-ack','lease-events','lease-complete','lease-reconcile','lease-artifact')),
            target_id TEXT NOT NULL,
            request_hash TEXT NOT NULL CHECK (
                length(request_hash) = 64 AND request_hash NOT GLOB '*[^0-9a-f]*'),
            response_status INTEGER NOT NULL CHECK (response_status BETWEEN 100 AND 599),
            response_body_json TEXT NOT NULL CHECK (json_valid(response_body_json)),
            accepted_at TEXT NOT NULL,
            PRIMARY KEY (owner_principal_id,host_id,message_id),
            UNIQUE (owner_principal_id,host_id,sequence),
            FOREIGN KEY (owner_principal_id,host_id) REFERENCES remote_hosts(owner_principal_id,host_id)
        );

        INSERT INTO host_accepted_messages(
            owner_principal_id,host_id,message_id,sequence,operation,target_id,
            request_hash,response_status,response_body_json,accepted_at)
        SELECT owner_principal_id,host_id,message_id,sequence,operation,target_id,
               request_hash,response_status,response_body_json,accepted_at
        FROM host_accepted_messages_v19;

        DROP TABLE host_accepted_messages_v19;

        CREATE TABLE host_artifacts (
            owner_principal_id TEXT NOT NULL,
            artifact_id TEXT NOT NULL,
            run_id TEXT NOT NULL,
            lease_id TEXT NOT NULL,
            action_id TEXT NULL,
            kind TEXT NOT NULL CHECK (kind = 'TEXT'),
            media_type TEXT NOT NULL CHECK (media_type = 'text/plain'),
            summary TEXT NOT NULL,
            size_bytes INTEGER NOT NULL CHECK (size_bytes BETWEEN 0 AND 262144),
            sha256 TEXT NOT NULL CHECK (
                length(sha256) = 64 AND sha256 NOT GLOB '*[^0-9a-f]*'),
            retention TEXT NOT NULL CHECK (retention = 'RUN'),
            content_state TEXT NOT NULL CHECK (content_state = 'AVAILABLE'),
            redacted INTEGER NOT NULL CHECK (redacted IN (0,1)),
            truncated INTEGER NOT NULL CHECK (truncated IN (0,1)),
            created_at TEXT NOT NULL,
            expires_at TEXT NULL,
            version INTEGER NOT NULL CHECK (version >= 1),
            PRIMARY KEY (owner_principal_id,artifact_id),
            FOREIGN KEY (owner_principal_id,run_id) REFERENCES job_runs(owner_principal_id,run_id),
            FOREIGN KEY (owner_principal_id,lease_id) REFERENCES host_work_leases(owner_principal_id,lease_id),
            FOREIGN KEY (owner_principal_id,action_id) REFERENCES actions(owner_principal_id,action_id)
        );

        CREATE TABLE host_artifact_contents (
            owner_principal_id TEXT NOT NULL,
            artifact_id TEXT NOT NULL,
            text_content TEXT NOT NULL,
            PRIMARY KEY (owner_principal_id,artifact_id),
            FOREIGN KEY (owner_principal_id,artifact_id)
                REFERENCES host_artifacts(owner_principal_id,artifact_id)
        );

        CREATE TABLE host_artifact_receipts (
            owner_principal_id TEXT NOT NULL,
            receipt_id TEXT NOT NULL,
            artifact_id TEXT NOT NULL,
            message_id TEXT NOT NULL,
            declared_size INTEGER NOT NULL CHECK (declared_size BETWEEN 0 AND 262144),
            declared_sha256 TEXT NOT NULL CHECK (
                length(declared_sha256) = 64 AND declared_sha256 NOT GLOB '*[^0-9a-f]*'),
            accepted_at TEXT NOT NULL,
            PRIMARY KEY (owner_principal_id,receipt_id),
            UNIQUE (owner_principal_id,artifact_id),
            FOREIGN KEY (owner_principal_id,artifact_id)
                REFERENCES host_artifacts(owner_principal_id,artifact_id)
        );

        CREATE TRIGGER trg_host_artifacts_validate_insert
        BEFORE INSERT ON host_artifacts
        WHEN NOT EXISTS(
            SELECT 1
            FROM host_work_leases lease
            WHERE lease.owner_principal_id = NEW.owner_principal_id
              AND lease.lease_id = NEW.lease_id
              AND lease.run_id = NEW.run_id)
        BEGIN
            SELECT RAISE(ABORT,'invalid Host artifact lease snapshot');
        END;

        CREATE INDEX ix_host_accepted_messages_owner_host_sequence
            ON host_accepted_messages(owner_principal_id,host_id,sequence);
        CREATE INDEX ix_host_artifacts_run_created
            ON host_artifacts(owner_principal_id,run_id,created_at DESC,artifact_id);
        CREATE INDEX ix_host_artifacts_lease_created
            ON host_artifacts(owner_principal_id,lease_id,created_at DESC,artifact_id);
        CREATE INDEX ix_host_artifact_receipts_artifact
            ON host_artifact_receipts(owner_principal_id,artifact_id,accepted_at);
        CREATE UNIQUE INDEX ux_evidence_host_artifact_source
            ON evidence(owner_principal_id,source_type,source_native_id)
            WHERE source_type = 'host.artifact';
        """;

    private const string Migration19 = """
        CREATE TABLE job_execution_policies (
            owner_principal_id TEXT NOT NULL,
            job_id TEXT NOT NULL,
            location TEXT NOT NULL CHECK (location IN ('SERVER','HOST','ANY_COMPATIBLE_HOST')),
            preferred_host_id TEXT NULL,
            required_capabilities_json TEXT NOT NULL CHECK (
                COALESCE(json_valid(required_capabilities_json)
                    AND json_type(required_capabilities_json) = 'array', 0)),
            required_resource_ids_json TEXT NOT NULL CHECK (
                COALESCE(json_valid(required_resource_ids_json)
                    AND json_type(required_resource_ids_json) = 'array', 0)),
            fallback_policy TEXT NOT NULL CHECK (fallback_policy = 'NONE'),
            version INTEGER NOT NULL CHECK (version >= 1),
            CHECK (COALESCE(
                (location='SERVER'
                    AND preferred_host_id IS NULL
                    AND json_array_length(required_capabilities_json)=0
                    AND json_array_length(required_resource_ids_json)=0)
                OR (location='HOST'
                    AND preferred_host_id IS NOT NULL
                    AND json_array_length(required_capabilities_json)=1
                    AND json_array_length(required_resource_ids_json)>=1)
                OR (location='ANY_COMPATIBLE_HOST'
                    AND preferred_host_id IS NULL
                    AND json_array_length(required_capabilities_json)=1
                    AND json_array_length(required_resource_ids_json)>=1),
                0)),
            PRIMARY KEY (owner_principal_id,job_id),
            FOREIGN KEY (owner_principal_id,job_id) REFERENCES jobs(owner_principal_id,job_id),
            FOREIGN KEY (owner_principal_id,preferred_host_id) REFERENCES remote_hosts(owner_principal_id,host_id)
        );

        CREATE TABLE job_run_blockers (
            owner_principal_id TEXT NOT NULL,
            run_id TEXT NOT NULL,
            code TEXT NOT NULL CHECK (code IN (
                'WAITING_FOR_HOST','WAITING_FOR_CAPABILITY','WAITING_FOR_RESOURCE',
                'HOST_DISCONNECTED','HOST_UPDATE_REQUIRED')),
            host_id TEXT NULL,
            capability_id TEXT NULL,
            resource_id TEXT NULL,
            detail_code TEXT NULL,
            observed_at TEXT NOT NULL,
            cleared_at TEXT NULL,
            version INTEGER NOT NULL CHECK (version >= 1),
            PRIMARY KEY (owner_principal_id,run_id,version),
            FOREIGN KEY (owner_principal_id,run_id) REFERENCES job_runs(owner_principal_id,run_id),
            FOREIGN KEY (owner_principal_id,host_id) REFERENCES remote_hosts(owner_principal_id,host_id)
        );

        CREATE TABLE host_work_leases (
            owner_principal_id TEXT NOT NULL,
            lease_id TEXT NOT NULL,
            run_id TEXT NOT NULL,
            job_id TEXT NOT NULL,
            host_id TEXT NOT NULL,
            scheduler_fence INTEGER NOT NULL CHECK (scheduler_fence >= 1),
            attempt INTEGER NOT NULL CHECK (attempt >= 1),
            profile_id TEXT NOT NULL,
            capability_id TEXT NOT NULL,
            capability_version TEXT NOT NULL,
            capability_grant_version INTEGER NOT NULL CHECK (capability_grant_version >= 1),
            input_hash TEXT NOT NULL CHECK (length(input_hash) = 64 AND input_hash NOT GLOB '*[^0-9a-f]*'),
            state TEXT NOT NULL CHECK (state IN (
                'OFFERED','ACKNOWLEDGED','RUNNING','COMPLETED','FAILED',
                'RECONCILIATION_REQUIRED','DECLINED','EXPIRED','REVOKED','DISCONNECTED')),
            issued_at TEXT NOT NULL,
            execute_until TEXT NOT NULL,
            acknowledged_at TEXT NULL,
            completed_at TEXT NULL,
            local_attempt_id TEXT NULL,
            outcome TEXT NULL CHECK (outcome IS NULL OR outcome IN ('SUCCEEDED','FAILED','UNKNOWN')),
            output_sha256 TEXT NULL CHECK (
                output_sha256 IS NULL OR (length(output_sha256) = 64 AND output_sha256 NOT GLOB '*[^0-9a-f]*')),
            failure_code TEXT NULL,
            version INTEGER NOT NULL CHECK (version >= 1),
            PRIMARY KEY (owner_principal_id,lease_id),
            UNIQUE (owner_principal_id,run_id,attempt),
            FOREIGN KEY (owner_principal_id,run_id) REFERENCES job_runs(owner_principal_id,run_id),
            FOREIGN KEY (owner_principal_id,job_id) REFERENCES jobs(owner_principal_id,job_id),
            FOREIGN KEY (owner_principal_id,host_id) REFERENCES remote_hosts(owner_principal_id,host_id),
            FOREIGN KEY (owner_principal_id,host_id,capability_id,capability_version,capability_grant_version)
                REFERENCES host_capability_grants(owner_principal_id,host_id,capability_id,capability_version,version)
        );

        CREATE TABLE host_lease_events (
            owner_principal_id TEXT NOT NULL,
            lease_id TEXT NOT NULL,
            event_id TEXT NOT NULL,
            sequence INTEGER NOT NULL CHECK (sequence >= 1),
            type TEXT NOT NULL CHECK (type IN (
                'HOST_CONNECTED','HOST_DISCONNECTED','JOB_ACCEPTED','STEP_STARTED',
                'STEP_COMPLETED','APPROVAL_REQUIRED','JOB_FAILED','JOB_COMPLETED')),
            occurred_at TEXT NOT NULL,
            summary TEXT NULL,
            data_json TEXT NULL CHECK (data_json IS NULL OR json_valid(data_json)),
            PRIMARY KEY (owner_principal_id,lease_id,event_id),
            UNIQUE (owner_principal_id,lease_id,sequence),
            FOREIGN KEY (owner_principal_id,lease_id) REFERENCES host_work_leases(owner_principal_id,lease_id)
        );

        CREATE TABLE host_lease_resources (
            owner_principal_id TEXT NOT NULL,
            lease_id TEXT NOT NULL,
            resource_id TEXT NOT NULL,
            resource_grant_version INTEGER NOT NULL CHECK (resource_grant_version >= 1),
            access_mode TEXT NOT NULL CHECK (access_mode = 'READ_ONLY'),
            fingerprint TEXT NOT NULL CHECK (
                length(fingerprint) = 64 AND fingerprint NOT GLOB '*[^0-9a-f]*'),
            PRIMARY KEY (owner_principal_id,lease_id,resource_id),
            FOREIGN KEY (owner_principal_id,lease_id) REFERENCES host_work_leases(owner_principal_id,lease_id)
        );

        ALTER TABLE actions ADD COLUMN host_id TEXT NULL CHECK (
            host_id IS NULL OR (
                length(host_id) <= 64
                AND host_id NOT GLOB '*[^a-z0-9-]*'
                AND substr(host_id,1,1) GLOB '[a-z0-9]'));
        ALTER TABLE actions ADD COLUMN host_lease_id TEXT NULL CHECK (
            host_lease_id IS NULL OR (
                length(host_lease_id) <= 64
                AND host_lease_id NOT GLOB '*[^a-z0-9-]*'
                AND substr(host_lease_id,1,1) GLOB '[a-z0-9]'));
        ALTER TABLE actions ADD COLUMN host_resource_grant_hash TEXT NULL CHECK (
            (host_id IS NULL AND host_lease_id IS NULL AND host_resource_grant_hash IS NULL)
            OR (host_id IS NOT NULL AND host_lease_id IS NOT NULL
                AND host_resource_grant_hash IS NOT NULL
                AND length(host_resource_grant_hash) = 64
                AND host_resource_grant_hash NOT GLOB '*[^0-9a-f]*'));

        ALTER TABLE action_authorizations ADD COLUMN host_id TEXT NULL CHECK (
            host_id IS NULL OR (
                length(host_id) <= 64
                AND host_id NOT GLOB '*[^a-z0-9-]*'
                AND substr(host_id,1,1) GLOB '[a-z0-9]'));
        ALTER TABLE action_authorizations ADD COLUMN host_lease_id TEXT NULL CHECK (
            host_lease_id IS NULL OR (
                length(host_lease_id) <= 64
                AND host_lease_id NOT GLOB '*[^a-z0-9-]*'
                AND substr(host_lease_id,1,1) GLOB '[a-z0-9]'));
        ALTER TABLE action_authorizations ADD COLUMN host_resource_grant_hash TEXT NULL CHECK (
            (host_id IS NULL AND host_lease_id IS NULL AND host_resource_grant_hash IS NULL)
            OR (host_id IS NOT NULL AND host_lease_id IS NOT NULL
                AND host_resource_grant_hash IS NOT NULL
                AND length(host_resource_grant_hash) = 64
                AND host_resource_grant_hash NOT GLOB '*[^0-9a-f]*'));

        CREATE INDEX ix_job_execution_policies_owner_location
            ON job_execution_policies(owner_principal_id,location,preferred_host_id,job_id);
        CREATE UNIQUE INDEX ux_remote_hosts_global_host_id ON remote_hosts(host_id);
        CREATE TRIGGER trg_job_execution_policies_validate_insert
        BEFORE INSERT ON job_execution_policies
        WHEN NEW.location<>'SERVER' AND COALESCE((
            json_extract(NEW.required_capabilities_json,'$[0].capabilityId')<>'host.repo.identity'
            OR json_extract(NEW.required_capabilities_json,'$[0].capabilityVersion')<>'1'
            OR json_remove(json_extract(NEW.required_capabilities_json,'$[0]'),'$.capabilityId','$.capabilityVersion')<>'{}'
            OR EXISTS(
                SELECT 1 FROM json_each(NEW.required_resource_ids_json)
                WHERE type<>'text' OR length(value)<1 OR length(value)>64
                    OR value GLOB '*[^a-z0-9-]*' OR substr(value,1,1) NOT GLOB '[a-z0-9]')
            OR (SELECT count(*) FROM json_each(NEW.required_resource_ids_json))
                <> (SELECT count(DISTINCT value) FROM json_each(NEW.required_resource_ids_json))),1)
        BEGIN
            SELECT RAISE(ABORT,'invalid Host execution policy');
        END;
        CREATE TRIGGER trg_job_execution_policies_validate_update
        BEFORE UPDATE ON job_execution_policies
        WHEN NEW.location<>'SERVER' AND COALESCE((
            json_extract(NEW.required_capabilities_json,'$[0].capabilityId')<>'host.repo.identity'
            OR json_extract(NEW.required_capabilities_json,'$[0].capabilityVersion')<>'1'
            OR json_remove(json_extract(NEW.required_capabilities_json,'$[0]'),'$.capabilityId','$.capabilityVersion')<>'{}'
            OR EXISTS(
                SELECT 1 FROM json_each(NEW.required_resource_ids_json)
                WHERE type<>'text' OR length(value)<1 OR length(value)>64
                    OR value GLOB '*[^a-z0-9-]*' OR substr(value,1,1) NOT GLOB '[a-z0-9]')
            OR (SELECT count(*) FROM json_each(NEW.required_resource_ids_json))
                <> (SELECT count(DISTINCT value) FROM json_each(NEW.required_resource_ids_json))),1)
        BEGIN
            SELECT RAISE(ABORT,'invalid Host execution policy');
        END;
        CREATE UNIQUE INDEX ux_job_run_blockers_active
            ON job_run_blockers(owner_principal_id,run_id)
            WHERE cleared_at IS NULL;
        CREATE INDEX ix_job_run_blockers_owner_observed
            ON job_run_blockers(owner_principal_id,observed_at DESC,run_id);
        CREATE UNIQUE INDEX ux_host_work_leases_active_run
            ON host_work_leases(owner_principal_id,run_id)
            WHERE state IN ('OFFERED','ACKNOWLEDGED','RUNNING','DISCONNECTED');
        CREATE UNIQUE INDEX ux_host_work_leases_active_host
            ON host_work_leases(owner_principal_id,host_id)
            WHERE state IN ('OFFERED','ACKNOWLEDGED','RUNNING','DISCONNECTED');
        CREATE INDEX ix_host_work_leases_host_state_expiry
            ON host_work_leases(owner_principal_id,host_id,state,execute_until,lease_id);
        CREATE INDEX ix_host_work_leases_run_state_expiry
            ON host_work_leases(owner_principal_id,run_id,state,execute_until,lease_id);
        CREATE TRIGGER trg_host_work_leases_validate_insert
        BEFORE INSERT ON host_work_leases
        WHEN NOT EXISTS(
            SELECT 1 FROM job_runs run
            JOIN jobs job ON job.owner_principal_id=run.owner_principal_id AND job.job_id=run.job_id
            JOIN scheduler_leases scheduler ON scheduler.owner_principal_id=run.owner_principal_id
                AND scheduler.run_id=run.run_id AND scheduler.fence=NEW.scheduler_fence
            JOIN remote_hosts host ON host.owner_principal_id=run.owner_principal_id
                AND host.host_id=NEW.host_id AND host.lifecycle IN ('ONLINE','DEGRADED')
            JOIN host_capability_grants capability ON capability.owner_principal_id=host.owner_principal_id
                AND capability.host_id=host.host_id AND capability.capability_id=NEW.capability_id
                AND capability.capability_version=NEW.capability_version
                AND capability.version=NEW.capability_grant_version AND capability.revoked_at IS NULL
            WHERE run.owner_principal_id=NEW.owner_principal_id AND run.run_id=NEW.run_id
                AND run.job_id=NEW.job_id AND run.state='QUEUED'
                AND job.desired_state='ACTIVE' AND scheduler.expires_at>NEW.issued_at)
        BEGIN
            SELECT RAISE(ABORT,'invalid Host lease snapshot');
        END;
        CREATE TRIGGER trg_host_lease_resources_validate_insert
        BEFORE INSERT ON host_lease_resources
        WHEN NOT EXISTS(
            SELECT 1 FROM host_work_leases lease
            JOIN host_resource_grants grant_row
                ON grant_row.owner_principal_id=lease.owner_principal_id
                AND grant_row.host_id=lease.host_id
                AND grant_row.resource_id=NEW.resource_id
                AND grant_row.version=NEW.resource_grant_version
                AND grant_row.access_mode=NEW.access_mode
                AND grant_row.revoked_at IS NULL
            JOIN host_resources resource
                ON resource.owner_principal_id=grant_row.owner_principal_id
                AND resource.host_id=grant_row.host_id
                AND resource.resource_id=grant_row.resource_id
                AND resource.fingerprint=NEW.fingerprint
                AND resource.state='AVAILABLE'
            WHERE lease.owner_principal_id=NEW.owner_principal_id AND lease.lease_id=NEW.lease_id)
        BEGIN
            SELECT RAISE(ABORT,'invalid Host lease resource snapshot');
        END;
        CREATE INDEX ix_host_lease_events_lease_sequence
            ON host_lease_events(owner_principal_id,lease_id,sequence);
        """;

    private const string Migration18 = """
        CREATE TABLE host_pairings (
            owner_principal_id TEXT NOT NULL,
            pairing_id TEXT NOT NULL,
            claim_secret_hash TEXT NOT NULL CHECK (
                length(claim_secret_hash) = 64 AND claim_secret_hash NOT GLOB '*[^0-9a-f]*'),
            state TEXT NOT NULL CHECK (state IN ('ISSUED','CLAIMED','CONFIRMED','EXPIRED','CANCELED')),
            failed_claims INTEGER NOT NULL CHECK (failed_claims BETWEEN 0 AND 5),
            failed_confirmations INTEGER NOT NULL CHECK (failed_confirmations BETWEEN 0 AND 5),
            requested_host_json TEXT NULL CHECK (requested_host_json IS NULL OR (json_valid(requested_host_json) AND json_type(requested_host_json) = 'object')),
            created_at TEXT NOT NULL,
            expires_at TEXT NOT NULL,
            claimed_at TEXT NULL,
            confirmed_at TEXT NULL,
            canceled_at TEXT NULL,
            version INTEGER NOT NULL CHECK (version >= 1),
            PRIMARY KEY (owner_principal_id,pairing_id),
            UNIQUE (pairing_id),
            UNIQUE (owner_principal_id,claim_secret_hash),
            FOREIGN KEY (owner_principal_id) REFERENCES principals(principal_id)
        );

        CREATE TABLE remote_hosts (
            owner_principal_id TEXT NOT NULL,
            host_id TEXT NOT NULL,
            display_name TEXT NOT NULL,
            platform TEXT NOT NULL CHECK (platform = 'macOS'),
            architecture TEXT NOT NULL CHECK (architecture IN ('arm64','x86_64')),
            lifecycle TEXT NOT NULL CHECK (lifecycle IN ('PAIRING','ONLINE','BUSY','DEGRADED','OFFLINE','REVOKED','UPDATE_REQUIRED')),
            connection_status TEXT NOT NULL,
            public_key_jwk TEXT NOT NULL CHECK (COALESCE((
                json_valid(public_key_jwk)
                AND json_type(public_key_jwk) = 'object'
                AND json_extract(public_key_jwk,'$.crv') = 'P-256'
                AND json_extract(public_key_jwk,'$.kty') = 'EC'
                AND length(json_extract(public_key_jwk,'$.x')) = 43
                AND json_extract(public_key_jwk,'$.x') NOT GLOB '*[^A-Za-z0-9_-]*'
                AND length(json_extract(public_key_jwk,'$.y')) = 43
                AND json_extract(public_key_jwk,'$.y') NOT GLOB '*[^A-Za-z0-9_-]*'
                AND json_remove(public_key_jwk,'$.crv','$.kty','$.x','$.y') = '{}'
                AND public_key_jwk = '{"crv":"P-256","kty":"EC","x":"'
                    || json_extract(public_key_jwk,'$.x') || '","y":"'
                    || json_extract(public_key_jwk,'$.y') || '"}'
            ), 0)),
            key_version INTEGER NOT NULL CHECK (key_version >= 1),
            protection TEXT NOT NULL CHECK (protection IN ('SECURE_ENCLAVE','KEYCHAIN_THIS_DEVICE_ONLY')),
            agent_version TEXT NOT NULL,
            protocol_version TEXT NOT NULL CHECK (protocol_version = '1'),
            capability_catalog_version INTEGER NOT NULL CHECK (capability_catalog_version >= 1),
            last_accepted_sequence INTEGER NOT NULL CHECK (last_accepted_sequence >= 0),
            last_seen_at TEXT NULL,
            paired_at TEXT NOT NULL,
            revoked_at TEXT NULL,
            version INTEGER NOT NULL CHECK (version >= 1),
            PRIMARY KEY (owner_principal_id,host_id),
            UNIQUE (owner_principal_id,public_key_jwk),
            FOREIGN KEY (owner_principal_id) REFERENCES principals(principal_id)
        );

        CREATE TABLE host_capability_advertisements (
            owner_principal_id TEXT NOT NULL,
            host_id TEXT NOT NULL,
            capability_id TEXT NOT NULL CHECK (capability_id = 'host.repo.identity'),
            capability_version TEXT NOT NULL CHECK (capability_version = '1'),
            schema_hash TEXT NOT NULL CHECK (
                length(schema_hash) = 64 AND schema_hash NOT GLOB '*[^0-9a-f]*'),
            side_effect_class TEXT NOT NULL CHECK (side_effect_class = 'READ_ONLY'),
            advertised_at TEXT NOT NULL,
            PRIMARY KEY (owner_principal_id,host_id,capability_id,capability_version),
            FOREIGN KEY (owner_principal_id,host_id) REFERENCES remote_hosts(owner_principal_id,host_id)
        );

        CREATE TABLE host_capability_grants (
            owner_principal_id TEXT NOT NULL,
            host_id TEXT NOT NULL,
            capability_id TEXT NOT NULL,
            capability_version TEXT NOT NULL,
            granted_at TEXT NOT NULL,
            revoked_at TEXT NULL,
            version INTEGER NOT NULL CHECK (version >= 1),
            PRIMARY KEY (owner_principal_id,host_id,capability_id,capability_version,version),
            FOREIGN KEY (owner_principal_id,host_id,capability_id,capability_version)
                REFERENCES host_capability_advertisements(owner_principal_id,host_id,capability_id,capability_version)
        );

        CREATE TABLE host_resources (
            owner_principal_id TEXT NOT NULL,
            host_id TEXT NOT NULL,
            resource_id TEXT NOT NULL,
            type TEXT NOT NULL CHECK (type = 'REPOSITORY'),
            display_name TEXT NOT NULL,
            fingerprint TEXT NOT NULL CHECK (
                length(fingerprint) = 64 AND fingerprint NOT GLOB '*[^0-9a-f]*'),
            state TEXT NOT NULL CHECK (state = 'AVAILABLE'),
            advertised_at TEXT NOT NULL,
            version INTEGER NOT NULL CHECK (version >= 1),
            PRIMARY KEY (owner_principal_id,host_id,resource_id),
            FOREIGN KEY (owner_principal_id,host_id) REFERENCES remote_hosts(owner_principal_id,host_id)
        );

        CREATE TABLE host_resource_grants (
            owner_principal_id TEXT NOT NULL,
            host_id TEXT NOT NULL,
            resource_id TEXT NOT NULL,
            access_mode TEXT NOT NULL CHECK (access_mode = 'READ_ONLY'),
            granted_at TEXT NOT NULL,
            revoked_at TEXT NULL,
            version INTEGER NOT NULL CHECK (version >= 1),
            PRIMARY KEY (owner_principal_id,host_id,resource_id,version),
            FOREIGN KEY (owner_principal_id,host_id,resource_id)
                REFERENCES host_resources(owner_principal_id,host_id,resource_id)
        );

        CREATE TABLE host_accepted_messages (
            owner_principal_id TEXT NOT NULL,
            host_id TEXT NOT NULL,
            message_id TEXT NOT NULL,
            sequence INTEGER NOT NULL CHECK (sequence >= 1),
            operation TEXT NOT NULL CHECK (operation IN (
                'poll','lease-ack','lease-events','lease-complete','lease-reconcile')),
            target_id TEXT NOT NULL,
            request_hash TEXT NOT NULL CHECK (
                length(request_hash) = 64 AND request_hash NOT GLOB '*[^0-9a-f]*'),
            response_status INTEGER NOT NULL CHECK (response_status BETWEEN 100 AND 599),
            response_body_json TEXT NOT NULL CHECK (json_valid(response_body_json)),
            accepted_at TEXT NOT NULL,
            PRIMARY KEY (owner_principal_id,host_id,message_id),
            UNIQUE (owner_principal_id,host_id,sequence),
            FOREIGN KEY (owner_principal_id,host_id) REFERENCES remote_hosts(owner_principal_id,host_id)
        );

        CREATE INDEX ix_host_pairings_owner_state_expiry
            ON host_pairings(owner_principal_id,state,expires_at,pairing_id);
        CREATE INDEX ix_remote_hosts_owner_lifecycle_paired
            ON remote_hosts(owner_principal_id,lifecycle,paired_at,host_id);
        CREATE UNIQUE INDEX ux_host_capability_grants_active
            ON host_capability_grants(
                owner_principal_id,host_id,capability_id,capability_version)
            WHERE revoked_at IS NULL;
        CREATE UNIQUE INDEX ux_host_resource_grants_active
            ON host_resource_grants(owner_principal_id,host_id,resource_id)
            WHERE revoked_at IS NULL;
        """;

    private const string Migration16 = """
        ALTER TABLE execution_events RENAME TO execution_events_v15;
        DROP INDEX ix_events_owner_execution_sequence;
        CREATE TABLE execution_events (
            owner_principal_id TEXT NOT NULL, event_id TEXT NOT NULL, execution_id TEXT NOT NULL,
            sequence INTEGER NOT NULL CHECK (sequence > 0),
            event_type TEXT NOT NULL CHECK (event_type IN (
                'status','text','capability_requested','approval_required','capability_result','failure','completed',
                'realtime_negotiated','realtime_ended','realtime_turn_saved')),
            occurred_at TEXT NOT NULL, message_id TEXT NULL, capability_call_id TEXT NULL,
            action_id TEXT NULL, data_json TEXT NOT NULL,
            PRIMARY KEY (owner_principal_id, event_id),
            FOREIGN KEY (owner_principal_id) REFERENCES principals(principal_id),
            UNIQUE (owner_principal_id, execution_id, sequence)
        );
        INSERT INTO execution_events(owner_principal_id,event_id,execution_id,sequence,event_type,
            occurred_at,message_id,capability_call_id,action_id,data_json)
        SELECT owner_principal_id,event_id,execution_id,sequence,event_type,occurred_at,message_id,
            capability_call_id,action_id,data_json FROM execution_events_v15;
        DROP TABLE execution_events_v15;
        CREATE INDEX ix_events_owner_execution_sequence
            ON execution_events(owner_principal_id,execution_id,sequence);

        CREATE TABLE realtime_session_receipts (
            owner_principal_id TEXT NOT NULL,
            session_id TEXT NOT NULL,
            conversation_id TEXT NOT NULL,
            client_attempt_id TEXT NOT NULL,
            idempotency_key_hash TEXT NOT NULL,
            offer_hash TEXT NOT NULL,
            state TEXT NOT NULL CHECK (state IN ('NEGOTIATING','NEGOTIATED','CLIENT_ENDED','EXPIRED','FAILED')),
            negotiation_generation INTEGER NOT NULL CHECK (negotiation_generation >= 1),
            negotiation_deadline TEXT NOT NULL,
            provider_model_id TEXT NOT NULL,
            provider_model_version TEXT NOT NULL,
            provider_deployment_ref TEXT NOT NULL,
            negotiated_at TEXT NULL,
            expires_at TEXT NOT NULL,
            ended_at TEXT NULL,
            end_reason TEXT NULL,
            failure_code TEXT NULL,
            version INTEGER NOT NULL CHECK (version >= 1),
            PRIMARY KEY (owner_principal_id,session_id),
            UNIQUE (owner_principal_id,client_attempt_id),
            UNIQUE (owner_principal_id,idempotency_key_hash),
            FOREIGN KEY (owner_principal_id,conversation_id)
                REFERENCES conversations(owner_principal_id,conversation_id)
        );

        CREATE TABLE realtime_session_tools (
            owner_principal_id TEXT NOT NULL,
            session_id TEXT NOT NULL,
            exposed_name TEXT NOT NULL,
            plugin_id TEXT NOT NULL,
            plugin_version TEXT NOT NULL,
            capability_id TEXT NOT NULL,
            capability_version TEXT NOT NULL,
            account_id TEXT NULL,
            schema_hash TEXT NOT NULL,
            side_effect_class TEXT NOT NULL,
            PRIMARY KEY (owner_principal_id,session_id,exposed_name),
            FOREIGN KEY (owner_principal_id,session_id)
                REFERENCES realtime_session_receipts(owner_principal_id,session_id),
            FOREIGN KEY (owner_principal_id,account_id)
                REFERENCES connected_accounts(owner_principal_id,account_id)
        );

        CREATE TABLE realtime_turn_receipts (
            owner_principal_id TEXT NOT NULL,
            session_id TEXT NOT NULL,
            client_turn_id TEXT NOT NULL,
            input_item_id TEXT NOT NULL,
            output_item_id TEXT NULL,
            user_message_id TEXT NOT NULL,
            assistant_message_id TEXT NULL,
            assistant_disposition TEXT NOT NULL CHECK (assistant_disposition IN ('COMPLETED','INTERRUPTED','FAILED')),
            created_at TEXT NOT NULL,
            PRIMARY KEY (owner_principal_id,session_id,client_turn_id),
            UNIQUE (owner_principal_id,session_id,input_item_id),
            UNIQUE (owner_principal_id,session_id,output_item_id),
            FOREIGN KEY (owner_principal_id,session_id)
                REFERENCES realtime_session_receipts(owner_principal_id,session_id),
            FOREIGN KEY (owner_principal_id,user_message_id)
                REFERENCES messages(owner_principal_id,message_id),
            FOREIGN KEY (owner_principal_id,assistant_message_id)
                REFERENCES messages(owner_principal_id,message_id)
        );

        CREATE TABLE realtime_tool_bindings (
            owner_principal_id TEXT NOT NULL,
            session_id TEXT NOT NULL,
            client_call_id TEXT NOT NULL,
            capability_call_id TEXT NULL,
            capability_result_id TEXT NULL,
            action_id TEXT NULL,
            state TEXT NOT NULL CHECK (state IN ('REQUESTED','RUNNING','APPROVAL_REQUIRED','COMPLETED','FAILED','RECONCILIATION_REQUIRED')),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            version INTEGER NOT NULL CHECK (version >= 1),
            PRIMARY KEY (owner_principal_id,session_id,client_call_id),
            FOREIGN KEY (owner_principal_id,session_id)
                REFERENCES realtime_session_receipts(owner_principal_id,session_id),
            FOREIGN KEY (owner_principal_id,capability_call_id)
                REFERENCES capability_calls(owner_principal_id,call_id),
            FOREIGN KEY (owner_principal_id,capability_result_id)
                REFERENCES capability_results(owner_principal_id,result_id),
            FOREIGN KEY (owner_principal_id,action_id)
                REFERENCES actions(owner_principal_id,action_id)
        );

        CREATE INDEX ix_realtime_sessions_owner_conversation_state
            ON realtime_session_receipts(owner_principal_id,conversation_id,state,expires_at);
        CREATE INDEX ix_realtime_tools_owner_capability
            ON realtime_session_tools(owner_principal_id,plugin_id,plugin_version,capability_id,capability_version,account_id);
        """;

    private const string Migration17 = """
        ALTER TABLE jobs ADD COLUMN kind TEXT NOT NULL DEFAULT 'AUTOMATION'
            CHECK (kind IN ('AUTOMATION','DEVELOPMENT'));
        ALTER TABLE jobs ADD COLUMN conversation_id TEXT NULL;

        CREATE UNIQUE INDEX ux_jobs_owner_job_conversation
            ON jobs(owner_principal_id,job_id,conversation_id);

        CREATE TABLE development_workspaces (
            owner_principal_id TEXT NOT NULL,
            workspace_id TEXT NOT NULL,
            conversation_id TEXT NOT NULL,
            display_name TEXT NOT NULL,
            snapshot_ref TEXT NOT NULL,
            snapshot_hash TEXT NOT NULL,
            state TEXT NOT NULL CHECK (state IN ('READY','REVOKED')),
            created_at TEXT NOT NULL,
            version INTEGER NOT NULL CHECK (version >= 1),
            PRIMARY KEY (owner_principal_id,workspace_id),
            UNIQUE (owner_principal_id,workspace_id,conversation_id),
            FOREIGN KEY (owner_principal_id,conversation_id)
                REFERENCES conversations(owner_principal_id,conversation_id)
        );

        CREATE TABLE development_job_specs (
            owner_principal_id TEXT NOT NULL,
            job_id TEXT NOT NULL,
            conversation_id TEXT NOT NULL,
            workspace_id TEXT NOT NULL,
            command_profile TEXT NOT NULL,
            arguments_json TEXT NOT NULL CHECK (json_valid(arguments_json) AND json_type(arguments_json) = 'array'),
            effect TEXT NOT NULL CHECK (effect IN ('READ_ONLY','WORKSPACE_WRITE')),
            timeout_seconds INTEGER NOT NULL CHECK (timeout_seconds BETWEEN 1 AND 3600),
            output_limit_bytes INTEGER NOT NULL CHECK (output_limit_bytes BETWEEN 1 AND 32768),
            executor_image_digest TEXT NOT NULL CHECK (length(executor_image_digest) > 0),
            PRIMARY KEY (owner_principal_id,job_id),
            FOREIGN KEY (owner_principal_id,job_id,conversation_id)
                REFERENCES jobs(owner_principal_id,job_id,conversation_id),
            FOREIGN KEY (owner_principal_id,workspace_id,conversation_id)
                REFERENCES development_workspaces(owner_principal_id,workspace_id,conversation_id)
        );

        CREATE INDEX ix_development_workspaces_owner_conversation_state
            ON development_workspaces(owner_principal_id,conversation_id,state,created_at,workspace_id);
        CREATE INDEX ix_development_specs_owner_workspace
            ON development_job_specs(owner_principal_id,workspace_id,job_id);
        """;

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