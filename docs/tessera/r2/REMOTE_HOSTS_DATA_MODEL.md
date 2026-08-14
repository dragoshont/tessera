# R2 Remote Hosts Data And Migration Contract

**Status:** Accepted phased additive contract. Migrations v18-v19 are
implemented; migration v20 remains planned and is not current product capability.

Remote adds Host bindings around canonical owner, Job, JobRun, Action, Evidence,
Activity, output and scheduler records. It does not add Host-owned Jobs, Memory,
policy, approvals, or provider Accounts.

## Migration v18: identity, pairing and grants

```text
host_pairings
  ownerPrincipalId, pairingId, claimSecretHash, state,
  failedClaims, failedConfirmations, requestedHostJson,
  createdAt, expiresAt, claimedAt, confirmedAt, canceledAt, version

remote_hosts
  ownerPrincipalId, hostId, displayName, platform, architecture,
  lifecycle, connectionStatus, publicKeyJwk, keyVersion, protection,
  agentVersion, protocolVersion, capabilityCatalogVersion,
  lastAcceptedSequence, lastSeenAt, pairedAt, revokedAt, version

host_capability_advertisements
  ownerPrincipalId, hostId, capabilityId, capabilityVersion,
  schemaHash, sideEffectClass, advertisedAt

host_capability_grants
  ownerPrincipalId, hostId, capabilityId, capabilityVersion,
  grantedAt, revokedAt, version

host_resources
  ownerPrincipalId, hostId, resourceId, type, displayName,
  fingerprint, state, advertisedAt, version

host_resource_grants
  ownerPrincipalId, hostId, resourceId, accessMode,
  grantedAt, revokedAt, version

host_accepted_messages
  ownerPrincipalId, hostId, messageId, sequence, operation, targetId,
  requestHash, responseStatus, responseBodyJson, acceptedAt
```

Pairing states are closed:

```text
ISSUED -> CLAIMED -> CONFIRMED
ISSUED|CLAIMED -> EXPIRED|CANCELED
```

Host lifecycle is `PAIRING|ONLINE|BUSY|DEGRADED|OFFLINE|REVOKED|UPDATE_REQUIRED`.
`connectionStatus` is a projection source value, not proof of completed work.

The claim secret is stored only as a hash. The visual confirmation code is
recomputed from pairing ID and RFC 7638 key thumbprint and is never persisted. Public key JWK
is non-secret but internal; public Host DTOs omit it. A unique owner/pairing secret
hash and owner/Host public-key thumbprint prevent duplicate enrollment.

Grant rows are append-only intervals. Their primary key includes `version`; a
partial unique index permits only one active (`revokedAt IS NULL`) grant for each
Host/capability-version or Host/resource. Removing a grant closes the active row.
Re-granting inserts a new row with the next version and never clears or overwrites
historical `grantedAt`/`revokedAt` values.

Migration CHECK constraints independently enforce proof-slice closed values and
canonical storage: 64 lowercase-hex hashes/fingerprints; canonical JSON JWK plus
application validation; platform `macOS`; architecture `arm64|x86_64`; protocol
version `1`; protection `SECURE_ENCLAVE|KEYCHAIN_THIS_DEVICE_ONLY`; capability
`host.repo.identity@1`; side effect `READ_ONLY`; resource type `REPOSITORY`;
resource state `AVAILABLE`; access mode `READ_ONLY`; and closed accepted-message
operations.

## Migration v19: execution policy, blockers and Host leases

Extend the existing immutable `ActionR2Binding` and matching `actions` /
`action_authorizations` columns with nullable `hostId`, `hostLeaseId`, and
`hostResourceGrantHash`. Sort resources by ASCII `resourceId`, reject duplicates,
and encode each tuple as UTF-8
`resourceId + "\n" + decimalGrantVersion + "\n" + accessMode + "\n" +
lowercaseHexFingerprint + "\n"`; concatenate without another separator and use
lowercase-hex SHA-256. Sort is unsigned bytewise lexicographic order (identical to
ordinal ASCII for the allowed IDs). Resource IDs match
`[a-z0-9][a-z0-9-]{0,63}` and therefore cannot contain separators; proof-slice
access mode is exactly `READ_ONLY`; grant version is `[1-9][0-9]{0,18}` within
positive Int64; fingerprint is exactly 64 lowercase hex characters. Host-backed consequential
Actions require all three values; non-Host Actions keep them null. Authorization
issuance copies the exact values and atomic consumption compares all three with
`IS`, alongside the existing capability/account/plugin/target/payload/execution
binding. Any Host, lease or resource substitution denies replay.

```text
job_execution_policies
  ownerPrincipalId, jobId, location, preferredHostId,
  requiredCapabilitiesJson, requiredResourceIdsJson,
  fallbackPolicy, version

job_run_blockers
  ownerPrincipalId, runId, code, hostId, capabilityId,
  resourceId, detailCode, observedAt, clearedAt, version

host_work_leases
  ownerPrincipalId, leaseId, runId, jobId, hostId,
  schedulerFence, attempt, profileId, capabilityId,
  capabilityVersion, capabilityGrantVersion,
  inputHash, state, issuedAt, executeUntil,
  acknowledgedAt, completedAt, localAttemptId, outcome,
  outputSha256, failureCode, version

host_lease_events
  ownerPrincipalId, leaseId, eventId, sequence, type,
  occurredAt, summary, dataJson

host_lease_resources
  ownerPrincipalId, leaseId, resourceId, resourceGrantVersion,
  accessMode, fingerprint
```

Execution policy locations are `SERVER|HOST|ANY_COMPATIBLE_HOST`; the proof slice
supports explicit `HOST` and deterministic `ANY_COMPATIBLE_HOST`. Fallback is
`NONE` in the proof slice. A Host/resource grant
can be revoked without rewriting historical policy.

Lease states:

```text
OFFERED -> ACKNOWLEDGED -> RUNNING -> COMPLETED|FAILED|RECONCILIATION_REQUIRED
OFFERED -> DECLINED|EXPIRED|REVOKED
ACKNOWLEDGED|RUNNING -> DISCONNECTED|REVOKED|RECONCILIATION_REQUIRED
DISCONNECTED -> RUNNING|EXPIRED|RECONCILIATION_REQUIRED
```

Creating or offering a Host lease does not start the canonical run. A valid Host
acknowledgement atomically changes `OFFERED -> ACKNOWLEDGED`, clears the blocker,
and uses the existing scheduler fence to change `JobRun QUEUED -> RUNNING`.
`STEP_STARTED` changes the lease to `RUNNING` only. Accepted lease completion uses
the same fence to drive the existing `JobRun RUNNING -> SUCCEEDED|FAILED`; unknown
outcome drives `RECONCILIATION_REQUIRED`. Decline/expiry before acknowledgement
leaves or returns the JobRun `QUEUED` with a blocker.

After acknowledgement, `HOST_DISCONNECTED` keeps the JobRun `RUNNING` and adds a
blocker until reconciliation or lease expiry. A signed reconciliation of
`NOT_STARTED` before expiry may atomically expire the lease and return the JobRun
to `QUEUED`; that lease is terminal and no new lease is issued until a fresh
scheduler fence is acquired. Reconnect while the original lease is unexpired may
resume that same lease only after exact local-attempt reconciliation; this is not
a reoffer. Requeue always creates a new lease ID under the fresh fence.
`STARTED|UNKNOWN` remains `RUNNING` while the lease is live and becomes
`RECONCILIATION_REQUIRED` at expiry. Revoking an `OFFERED` lease leaves `QUEUED`;
revoking `ACKNOWLEDGED` returns `QUEUED` only after an already-accepted signed
reconciliation from the same lease/local attempt says `NOT_STARTED`; absence of
`STEP_STARTED` alone is never proof. Without that proof, `ACKNOWLEDGED`, `RUNNING`
and `DISCONNECTED` yield `RECONCILIATION_REQUIRED`. No terminal canonical run is
reopened or reoffered.

One active Host lease per Run and per Host is enforced. The scheduler fence,
capability/grant versions, resource grant versions/access modes/fingerprints and
input hash are immutable snapshots. Ack, event and completion recheck every
snapshotted grant at the exact active version. Terminal results are exact-replay
only. A later fence, changed hash, expired lease, revoked/changed grant, wrong
Host, or changed local attempt cannot complete the canonical run.

Blocker codes are closed:

```text
WAITING_FOR_HOST | WAITING_FOR_CAPABILITY | WAITING_FOR_RESOURCE |
HOST_DISCONNECTED | HOST_UPDATE_REQUIRED
```

A blocker is owner/run scoped, has one active row per run, and is cleared rather
than deleted so history can be projected to Activity.

## Planned migration v20: artifact metadata and receipts

```text
host_artifacts
  ownerPrincipalId, artifactId, runId, leaseId, actionId,
  kind, mediaType, summary, sizeBytes, sha256, retention,
  contentState, redacted, truncated, createdAt, expiresAt, version

host_artifact_contents
  ownerPrincipalId, artifactId, textContent

host_artifact_receipts
  ownerPrincipalId, receiptId, artifactId, messageId,
  declaredSize, declaredSha256, acceptedAt
```

The proof slice accepts bounded UTF-8 text only. Both Host and server apply the
same order: strict UTF-8 decode, newline normalization, control removal, secret
redaction, byte truncation, then SHA-256 over the exact persisted bytes. Raw and
pre-redaction content is never persisted. The Host declares this post-normalization
size/hash; the server recomputes and requires an exact match. Binary upload, download
locators and chunking are future work. Content is never inserted into Memory or
Evidence automatically. Evidence may reference the artifact hash/locator only via
a separate canonical verification transition.

## Ownership and deletion

Every foreign key includes `ownerPrincipalId`. Pairing, Host, grants, resources,
leases, blockers and artifacts are inaccessible cross-owner and return the same
`not_found` boundary. Revocation is not deletion. Historical Host identity may be
retained for audit while public key material is disabled. No migration copies
filesystem paths, private keys, claim secrets, raw commands, environment, model
prompts, chain-of-thought, or unbounded logs.

## Rollback

Disable Host dispatch, expire active Host leases, preserve canonical Jobs/runs and
all additive tables, and deploy the prior binary. Server/Kubernetes execution and
unrelated Jobs continue. No down migration or destructive cleanup runs.
