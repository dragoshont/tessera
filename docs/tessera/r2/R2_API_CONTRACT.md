# R2 API Contract

## Public server descriptor

`GET /.well-known/tessera` is the only unauthenticated native bootstrap contract. A configured installation returns `200`, `Cache-Control: no-store`, and a body smaller than 4 KiB:

```json
{
	"product": "tessera",
	"serverId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
	"displayName": "Tessera Home",
	"serverVersion": "0.1.0",
	"apiVersion": "v1",
	"protocolVersion": 1
}
```

An unconfigured installation returns RFC 9457 Problem Details with HTTP `503` and `code=server_identity_unconfigured`. Clients reject redirects, malformed/oversized responses, wrong product/ID/version, non-HTTPS non-loopback origins and TLS failures before attaching an access token.

**Authority:** This document refines the normative requirements in `docs/tessera/r1/r2-spec.md`. `R2_PRODUCT_SPEC.md` is the route summary; this is the exact cross-tier contract.

## Common Wire Types

IDs are server-issued lowercase UUID strings. Timestamps are ISO-8601 UTC. Versions and SSE sequences are positive integers. JSON rejects unknown properties on mutation DTOs.

```text
Page<T>             { items: T[], nextCursor: string|null }
MutationReceipt     { resourceId: string, version: integer, replayed: boolean }
ProblemDetails      { type, title, status, detail, instance, code, traceId, stage? }
ResourceRef         { id: string, version: integer }
```

Cursors are opaque base64url encodings of the stable sort tuple plus owner-bound HMAC. Lists are limited to 1-100 (default 50). Invalid or other-owner cursors return `400 invalid_cursor`. Every route requires the existing authenticated user scope; `/settings/admin/**` additionally requires operator scope. The loopback development principal is available only when `Development` and explicit local-dev authentication are enabled and is rejected in production.

## DTOs

```text
ConversationSummary { id, title, state, modelProfileId|null, createdAt, updatedAt, version }
ConversationDetail  { ConversationSummary, messages: Page<MessageDto> }
MessageDto           { id, conversationId, role, status, parts: MessagePartDto[], createdAt, completedAt|null, retryOf|null, version }
MessagePartDto       { id, kind, text|null, capabilityCallId|null, capabilityResultId|null, actionId|null, evidenceRefs: string[], errorCode|null }
ExecutionEventDto    { sequence, executionId, type, occurredAt, messageId|null, capabilityCallId|null, actionId|null, data }
ConnectedAccountDto  { id, providerId, pluginId, displayName, identityHint|null, lifecycle, permissions: string[], capabilityIds: string[], health, lastSuccessfulUse|null, version }
PluginDto            { id, name, version, publisher, enabled, packageHash, accountProviderIds: string[], capabilities: CapabilityDto[], versionStamp }
CapabilityDto        { id, version, pluginId, description, accountRequired, requiredPermissions: string[], sideEffectClass, available, blockedCode|null }
PluginConfigurationDto { pluginId, pluginVersion, values: object, configured, version }
ActionDto             { id, conversationId|null, messageId|null, jobId|null, jobRunId|null, pluginId, pluginVersion, capabilityId, capabilityVersion, accountId|null, target, payloadPreview, state, expiresAt, providerReceipt|null, verificationState|null, failureCode|null, version }
JobDto                { id, name, instruction, desiredState, health, modelProfileId|null, schedule, nextOccurrence|null, accountGrants: string[], capabilityGrants: string[], sideEffectGrants: string[], contextPolicy, lastRun|null, version }
DevelopmentWorkspaceDto { id, conversationId, displayName, snapshotHash, state, createdAt, version }
DevelopmentSpecDto    { workspaceId, commandProfile, arguments: string[0..8] (each <= 256 UTF-8 bytes), effect, timeoutSeconds, outputLimitBytes: 32768 }
JobRunDto             { id, jobId, scheduledFor, state, startedAt|null, endedAt|null, modelProfileId|null, contextSnapshotRef|null, capabilityCallIds: string[], accountIds: string[], actionIds: string[], outputRefs: string[], evidenceRefs: string[], errorCode|null, version }
JobRunDetailDto       { run: JobRunDto, contextSnapshot: ContextSnapshotDto|null, capabilityUses: Page<CapabilityUseDto>, accountUses: Page<AccountUseDto>, actions: Page<ActionDto>, outputs: Page<JobRunOutputDto>, evidence: Page<EvidenceCitationDto>, trace: Page<ExecutionTraceEntryDto> }
ContextSnapshotDto    { ref, capturedAt, sourceRefs: string[], omittedCount, sensitivityClasses: string[] }
CapabilityUseDto      { callId, pluginId, pluginVersion, capabilityId, capabilityVersion, accountId|null, state, resultSummary|null, evidenceRefs: string[], errorCode|null }
AccountUseDto         { accountId, providerId, displayName, lifecycleAtDispatch }
JobRunOutputDto       { ref, kind: TEXT|ACTION|DEVELOPMENT_LOG, mediaType, summary, text|null, truncated, createdAt }
ExecutionTraceEntryDto { sequence, occurredAt, type, summary, capabilityCallId|null, actionId|null, approvalState|null, verificationState|null, outputRef|null, evidenceRefs: string[], errorCode|null }
MemoryDto             { assertionId, subjectKey, predicate, value, status, validFrom, validTo|null, evidenceRefs: string[], version }
MemoryChangeDto       { assertionId, kind, occurredAt, previous: MemoryDto|null, current: MemoryDto|null, evidenceRefs: string[] }
FollowUpDto           { id, status, fields: FollowUpFieldDto[], createdAt, updatedAt, version }
FollowUpFieldDto      { field, value, state, sourceTimestamp, evidenceRefs: string[] }
WhyDto                { assertionId, current, previous: MemoryDto|null, evidence: EvidenceCitationDto[], lineageRefs: string[] }
EvidenceCitationDto   { evidenceId, sourceType, sourceLocator, observedAt, sourceTimestamp|null, boundedExcerpt|null }
ActivityDto           { id, kind, occurredAt, summary, state|null, resourceType, resourceId, evidenceRefs: string[] }
SettingsDto            { defaultChatModelProfileId|null, defaultLightweightModelProfileId|null, timezone, approvalDefaults, memoryControls, version }
ModelProfileDto        { id, accountId, adapterKind, endpoint, model, contextLimit, supportsStreaming, supportsTools, enabled, createdAt, updatedAt, version }
SetupStatusDto          { server: SetupServerDto, ai: SetupAiDto, integrations: SetupIntegrationDto[], canOpenChat, requiredActionCount }
SetupServerDto          { state, displayName, version }
SetupAiDto              { state, gatewayId|null, displayName|null, model|null, profileId|null, detailCode|null }
SetupIntegrationDto     { id, name, state, runtimeState, accountId|null, accountHealth|null, detailCode|null, connectPath|null }
IntegrationSourceDto    { id, name, state, errorCode|null }
IntegrationCatalogDto   { id, name, description, source, publisher, runtime, repositoryOrPackage|null, version, license|null, trustLevel, capabilitiesSummary: string[], authTypes: string[], sensitivity, installationMode, installState, installed, inspectUrl|null }
IntegrationSearchDto    { items: IntegrationCatalogDto[], sources: IntegrationSourceDto[] }
```

`ExecutionEventDto.data` is type-specific and strict: `status` has `{ label }`; `text` has `{ delta }`; `capability_requested` has `{ capabilityCallId, capabilityId, accountId|null }`; `approval_required` has `{ actionId }`; `capability_result` has `{ capabilityResultId, summary, evidenceRefs }`; `failure` has `{ code, retryable }`; `completed` has `{ messageId }`. No hidden prompt or reasoning field exists.

## Operations

| Operation | Request | Success | Required preconditions / stable failures |
|---|---|---|---|
| `GET /conversations` | `state?, cursor?, limit?` | `200 Page<ConversationSummary>` | sort `(updatedAt DESC,id)` |
| `POST /conversations` | `{ title?, modelProfileId? }` + idempotency | `201 ConversationSummary` | profile owner/enabled; `422 configuration_required` |
| `GET /conversations/{id}` | none | `200 ConversationDetail` | first message page uses chronological order and common page limits |
| `PATCH /conversations/{id}` | `{ title?, state?, expectedVersion }` | `200 ConversationSummary` | ACTIVE/ARCHIVED only; `409 version_conflict` |
| `DELETE /conversations/{id}` | `{ expectedVersion }` | `204` | logical DELETED; `409 invalid_state` |
| `POST .../{id}/messages` | `{ text, modelProfileId?, accountPreferences?: object }` + idempotency | `202 { message, executionId }` | commits USER message first; `422 configuration_required/account_ambiguous` |
| `POST .../{id}/retry` | `{ messageId }` + idempotency | `202 { executionId }` | failed/stopped assistant turn only |
| `POST .../{id}/stop` | `{ executionId }` + idempotency | `202 MutationReceipt` | generation-bound cancellation |
| `GET .../{id}/events` | `after >= 0` | `text/event-stream` | owner/execution scoped; heartbeat comments allowed |
| `POST /accounts` | `{ pluginId, displayName, nonSecretConfig, secretInput }` + idempotency | `201 ConnectedAccountDto` | custody flow below; `422 invalid_configuration`; secret is never echoed |
| `POST /accounts/{id}/validate` | `{ expectedVersion }` + idempotency | `200 ConnectedAccountDto` | real adapter call; normalized provider errors |
| `POST /accounts/{id}/disable` | `{ expectedVersion }` | `200 ConnectedAccountDto` | terminal REVOKED cannot enable |
| `DELETE /accounts/{id}` | `{ expectedVersion }` | `202 ConnectedAccountDto` | immediately REVOKED; custody cleanup may be pending |
| `GET /plugins` | `enabled?, accountProviderId?, cursor?, limit?` | `200 Page<PluginDto>` | sort `(name,id,version)`; validated catalog installations only |
| `GET /plugins/{id}/versions/{version}` | none | `200 PluginDto` | exact installed semantic version; retained historical descriptor remains readable |
| `POST /plugins/{id}/versions/{version}/enable|disable` | `{ expectedVersion }` | `200 PluginDto` | exact installation; digest must match catalog; disable blocks new dispatch immediately |
| `GET /plugins/{id}/versions/{version}/configuration` | none | `200 PluginConfigurationDto` | values are manifest-declared non-secret fields only |
| `PUT /plugins/{id}/versions/{version}/configuration` | `{ values, expectedVersion }` | `200 PluginConfigurationDto` | strict manifest schema; unknown/secret-like fields rejected |
| `DELETE /plugins/{id}/versions/{version}` | `{ expectedVersion }` | `204` | `409 plugin_in_use` while account, active Job grant, pending Action, or retained execution reference exists; immutable historical descriptor retained |
| `GET /capabilities` | filters only | `200 Page<CapabilityDto>` | filtered server-side, unavailable reasons safe to disclose |
| `GET /actions` | `state?, conversationId?, messageId?, jobId?, jobRunId?, approvalRequired?, from?, to?, cursor?, limit?` | `200 Page<ActionDto>` | sort `(createdAt DESC,id)`; `approvalRequired=true` means current `PROPOSED` and unexpired |
| `GET /actions/{id}` | none | `200 ActionDto` | exact current durable Action state and verification projection |
| `POST /actions` | structured coordinator proposal + idempotency | `201 ActionDto` | clients/models cannot set authorization or success |
| `POST /actions/{id}/approve` | `{ expectedVersion }` + idempotency | `202 ActionDto` | exact current proposal, owner, expiry, plugin/account/grants |
| `POST /actions/{id}/cancel` | `{ expectedVersion }` | `200 ActionDto` | terminal states reject |
| `POST /jobs` | `{ name, instruction, schedule, modelProfileId?, grants, contextPolicy, desiredState }` + idempotency | `201 JobDto` | IANA timezone; explicit grants |
| `GET /jobs/{id}` | none | `200 JobDto` | exact current desired state, health, grants, schedule, and version |
| `PATCH /jobs/{id}` | mutable creation fields + `expectedVersion` | `200 JobDto` | edits do not mutate existing runs/actions |
| `DELETE /jobs/{id}` | `{ expectedVersion }` | `202 JobDto` | desired state CANCELED; active lease observes cancellation |
| `POST /jobs/{id}/run` | `{ expectedVersion }` + idempotency | `202 JobRunDto` | unique manual scheduledFor/idempotency receipt |
| `POST /jobs/{id}/pause|resume` | `{ expectedVersion }` | `200 JobDto` | legal desired-state transition only |
| `GET /conversations/{id}/development-workspaces` | `cursor?, limit?` | `200 Page<DevelopmentWorkspaceDto>` | owner/conversation-scoped READY server snapshots only |
| `POST /conversations/{id}/development-tasks` | `{ name, workspaceId, commandProfile, arguments }` + idempotency | `202 { job: JobDto, run: JobRunDto }` | owner-scoped READY server snapshot; server-resolved command profile; no client path/URL/image/executable; `422 development_command_not_allowed/development_executor_unavailable` |
| `GET /jobs/{id}/runs` | `state?, from?, to?, cursor?, limit?` | `200 Page<JobRunDto>` | sort `(scheduledFor DESC,id)`; interval is UTC and `from <= to` |
| `GET /job-runs/{id}` | none | `200 JobRunDetailDto` | bounded public product trace; no prompt, reasoning, secret, or raw provider body |
| `GET /job-runs/{id}/capability-uses` | `cursor?, limit?` | `200 Page<CapabilityUseDto>` | durable call sequence order |
| `GET /job-runs/{id}/account-uses` | `cursor?, limit?` | `200 Page<AccountUseDto>` | stable `(accountId)` order; lifecycle is dispatch-time snapshot |
| `GET /job-runs/{id}/actions` | `state?, approvalRequired?, cursor?, limit?` | `200 Page<ActionDto>` | same Action projection/filter semantics as `GET /actions` |
| `GET /job-runs/{id}/outputs` | `cursor?, limit?` | `200 Page<JobRunOutputDto>` | creation order; bounded product output only |
| `GET /job-runs/{id}/evidence` | `cursor?, limit?` | `200 Page<EvidenceCitationDto>` | creation order; owner-scoped citations only |
| `GET /job-runs/{id}/trace` | `afterSequence?, cursor?, limit?` | `200 Page<ExecutionTraceEntryDto>` | durable sequence order; public product events only |
| `GET /memory` | `query?, status?, subjectKey?, predicate?, includeHistory=false, cursor?, limit?` | `200 Page<MemoryDto>` | case-insensitive literal search over subject/predicate/value; sort `(validFrom DESC,assertionId)` |
| `POST /memory` | `{ subjectKey, predicate, value }` + idempotency | `201 MemoryDto` | explicit user Evidence/Assertion only |
| `POST /memory/{id}/correct` | `{ value, expectedVersion }` + idempotency | `201 MemoryDto` | atomically supersedes current |
| `GET /memory/{id}/history` | `cursor?, limit?` | `200 Page<MemoryChangeDto>` | chronological correction/stop-using projection with previous/current values and exact evidence refs |
| `GET /memory/{id}/why` | none | `200 WhyDto` | exact owner-scoped evidence and lineage projection; never generated rationale |
| `POST /memory/{id}/stop-using` | `{ expectedVersion }` | `200 MemoryDto` | excludes from context; does not claim erase |
| `GET /memory/follow-ups` | `query?, status?, field?, from?, to?, cursor?, limit?` | `200 Page<FollowUpDto>` | literal search over current field values; sort `(updatedAt DESC,id)`; source Evidence refs retained |
| `GET /activity` | `query?, kind?, state?, from?, to?, cursor?, limit?` | `200 Page<ActivityDto>` | kinds `evidence,event,follow_up,memory_change,action,job_run`; sort `(occurredAt DESC,id)`; bounded product-safe summaries only |
| `GET /settings` | none | `200 SettingsDto` | owner settings or documented defaults at version 1 |
| `PATCH /settings` | partial settings + `expectedVersion` | `200 SettingsDto` | referenced profiles owner/enabled |
| `GET /settings/model-profiles` | `enabled?, cursor?, limit?` | `200 Page<ModelProfileDto>` | owner profiles sorted `(updatedAt DESC,id)`; endpoint is non-secret and secret input is never returned |
| `POST /settings/model-profiles` | `{ accountId, adapterKind, endpoint, model, contextLimit }` + idempotency | `201 ModelProfileDto` | account owner/CONNECTED and adapter kind allowed; `422 configuration_required/invalid_model` |
| `GET /setup/status` | none | `200 SetupStatusDto` | derives model/account/plugin readiness from canonical state and provider-owned runtime descriptors |
| `POST /setup/bootstrap` | `{}` + idempotency | `200 SetupStatusDto` | per-owner serialized convergence; validates the configured gateway/model, custody and canonical bindings; `400 invalid_idempotency_key`, `422 model_gateway_*` |
| `GET /integrations/sources` | none | `200 { items: IntegrationSourceDto[] }` | source metadata only; no public source executes code |
| `GET /integrations/search` | `query, limit?` | `200 IntegrationSearchDto` | query 2-100 chars, limit 1-50; local hash-validated packages plus cached public metadata; source failures degrade independently |
| `POST /integrations/local/{id}/versions/{version}/install` | `{}` + idempotency | `200 { pluginId, version, installState: "INSTALLED" }` | exact hash-validated server package only; first install is disabled; package row and durable receipt commit atomically; replay returns exact body and `Idempotency-Replayed: true`; changed `id@version` returns `409 idempotency_conflict`; `404 reviewed_package_not_found`, `409 package_hash_conflict/package_previously_removed` |

GET list/detail routes return their named DTOs/Page DTOs and `404 not_found` without cross-owner disclosure. Endpoint-specific failures supplement the common authentication, validation, storage, and version errors.

List filters reject unknown enum values and malformed timestamps with `400 invalid_request`. `query` is trimmed, limited to 200 Unicode scalar values, and treated as literal text rather than SQL or a regular expression. `from` and `to` are inclusive UTC bounds. Plugin routes always address `(id, version)`; a bare plugin ID is never sufficient for mutation when versions coexist.

`POST /api/v1/capabilities/{id}/invoke` may add a coarse `stage` extension (`request`, `registry-account`, `registry-context`, `registry-capability`, `execution`, `evidence`, or `message`) to `400 invalid_request`. It never includes exception text, input, identity, or credential material.

`JobRunDetailDto` embeds the first page of each canonical child collection using the common default/max limits and supplies `nextCursor` when more exists. Output `text` is bounded to 32 KiB and `truncated=true` when more content exists; binary/provider payloads are never returned. `approvalState` and `verificationState` are projections of the referenced durable Action, not independently mutable run fields. `ContextSnapshotDto` exposes provenance references and omission counts only, not the hidden assembled model prompt.

Development Job DTOs add `kind=DEVELOPMENT`, the canonical `conversationId`, and
`developmentSpec`; existing Jobs return `kind=AUTOMATION`, `conversationId=null`,
and `developmentSpec=null`. The exact execution and isolation contract is
normative in `DEVELOPMENT_WORKSPACE.md`. Development output uses the existing
JobRun detail route and is redacted and bounded before persistence.

## Idempotency

Creation, dispatch, approval, validation, retry, and run-now require `Idempotency-Key` (1-128 visible ASCII). Scope is `(owner, route family, key)`. The server hashes canonical method, route resource IDs, and RFC 8785-style property-sorted JSON after validation; secret input is represented by its SHA-256 only in the transient hash computation and is never persisted. Exact replay returns the original status and exact documented response body and sets `Idempotency-Replayed: true`; an initial response sets `Idempotency-Replayed: false`. This header keeps entity DTO response bodies exact. Operations whose documented body is `MutationReceipt` also populate its `replayed` member consistently. A changed request returns `409 idempotency_conflict`. Receipts are retained at least as long as the referenced product record and never expire while an external outcome can be reconciled.

Setup bootstrap is a deterministic convergence operation rather than an external dispatch: it requires a valid key, serializes by owner in the one-writer server, rechecks canonical state inside that gate, uses deterministic account/profile IDs, and returns current setup state. Concurrent client calls cannot compensate or erase a winning credential. A later call repairs missing custody from server-owned configuration only after the real gateway/model probe succeeds.

Integration installation is deliberately narrower than discovery. Only `source=local` packages whose exact manifest bytes match the server's pinned catalog can reach the install route. Installation records the package disabled; enabling, configuration and account authorization remain separate explicit operations. MCP Registry and public repository metadata have no install route and cannot become executable through client-supplied source, URL, command, manifest, hash or trust fields.

## Credential Custody Transaction

The server creates Account ID and owner-bound opaque reference `r2/account/{SHA256(ownerPrincipalId)}/{accountId}`; clients cannot supply a reference. It writes the validated secret bundle through `ICredentialWriter.PutBundleAsync`, then commits metadata. If metadata commit fails it writes `CredentialBundle.Empty` as compensation. If compensation fails, no account becomes available; a secret-free cleanup receipt records only owner/ref/state and the API returns `503 storage_unavailable`.

Disconnect first commits `REVOKED`, making dispatch impossible, then writes `CredentialBundle.Empty`. Cleanup failure leaves lifecycle `REVOKED`, health `ERROR`, and a retryable cleanup receipt; it never restores availability. Every read checks that the deterministic owner-bound reference equals account metadata before custody access.

Credential references are logical owner-bound identifiers, not provider secret names. The Azure Key Vault store preserves already-compatible names and maps references containing `/` or other unsupported characters to `tessera-ref-{SHA256(reference)}` before SDK access. Product metadata retains only the logical reference; raw credentials and the original reference are never placed in a Key Vault URL.
