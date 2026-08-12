# Tessera Product Audit and MVP Definition

**Status:** Revised proposal for product sign-off
**Date:** 2026-08-09
**Source basis:** Tessera Product Vision, Architecture, and Development Specification v0.9; Tessera MVP Audit Required Adjustments (2026-08-09); repository code at `723aa31`; repository architecture records, tests, deployment examples, and the shipped web portal.

## Review and validation status

### Executable validation

- `./gates/checks.sh`: PASS on this revision; production web build and 73 web tests.
- `./gates/backend-checks.sh`: PASS on this revision; 546 .NET tests, Kubernetes render, 4/4 kubeconform resources, and deployment secret scan.
- No runtime deployment or external account was mutated.

These results validate only current broker/portal behavior covered by those suites. They do not validate the proposed product or its market value.

### Design review inputs

- GPT-5.4 semantic review of the previous audit.
- Claude Opus 4.8 semantic review of the previous audit.
- Tessera MVP Audit Required Adjustments, incorporated by this revision.
- Independent GPT-5.4 and Claude Opus 4.8 reviews of this revision: PASS on all A-01 through A-20 requirements, with no content blockers.

Model agreement is design feedback, not executable or product validation.

### Product validation

**Not yet established.** Product validation begins with Phase -1 and the read-only pilot.

## 1. Scope taxonomy

| Tag | Meaning |
|---|---|
| **CURRENT** | Implemented in the repository today. |
| **PHASE -1** | Product research before personal-source ingestion. |
| **MVP** | Read-only appointment continuity. |
| **MVP+1** | Calendar execution, built only after an explicit continue decision. |
| **LONG-TERM** | A possible later capability, not implied by MVP success. |
| **PROVISIONAL** | An unresolved direction requiring evidence or a separate decision. |

Repository ADRs and implementation issues SHOULD use these labels after product sign-off.

## 2. Executive decision

**CURRENT:** Tessera ships an identity-aware credential and action broker. It does not ship a personal-continuity product.

**MVP:** Proceed conditionally with **read-only appointment continuity** as the first product experiment:

> Tessera notices an appointment confirmation, reschedule, or cancellation in a user-selected Outlook source, preserves the accepted current and prior state, shows the evidence supporting each consequential field, and lets the user correct the interpretation.

**MVP+1:** Calendar reconciliation is the first execution extension. It is not required for MVP completion and is built only when the read-only experiment demonstrates recurring user value.

**LONG-TERM:** Generic Commitment, Claim, Entity, Situation, semantic retrieval, ontology, relationship learning, and generalized proactivity remain gated by measured product need.

> Tessera will first prove that durable, evidence-backed continuity creates user-visible value before proving execution. Persistent context is the product hypothesis; calendar mutation is a later extension.

## 3. Product invariants

1. Product pain MUST be demonstrated before platform complexity is justified.
2. Read-only continuity is sufficient to test the first Tessera hypothesis.
3. Execution is earned; it is not required to call the first experiment an MVP.
4. Tessera adds durable structure only when a measured workflow failure shows that a simpler representation is insufficient.
5. Abstractions are earned by repeated workflow need, not introduced because they appear in long-term architecture.
6. Appointment is modeled as `Appointment` in the appointment MVP.
7. Every consequential appointment field carries field-level provenance.
8. No cloud LLM receives email content in the MVP.
9. Trust-boundary blockers are development-order blockers, not release-only blockers.
10. Provider verification is not real-world confirmation.
11. Tessera mutates only calendar objects it created or the user explicitly adopted.
12. Pilot completion depends on observed event coverage, not elapsed time alone.
13. Tessera MUST demonstrate compounding value from Tessera-owned persisted state.
14. MVP success validates continuity and evidence in one domain, not the full personal-world-model thesis.

No task may add a graph database, generic ontology, generalized Situation, relationship inference, or semantic retrieval without linking it to a measured product failure and an approved experiment.

## 4. Hypotheses tested

### MVP validates

- recurring appointment-continuity pain;
- bounded ingestion, evidence, revisions, and current-versus-history behavior;
- field-level provenance, correction, and supersession;
- restart-safe state and reversible correlation;
- narrow deterministic/local extraction;
- user trust in field-level "Why?";
- whether persisted state reduces repeated work or stale understanding.

### MVP+1 additionally validates

- deterministic policy and content-bound approval;
- calendar ownership/adoption;
- idempotent mutation and unknown-outcome reconciliation;
- provider-state verification and conflict handling.

### This experiment does not validate

- broad entity/relationship learning or cross-domain world modeling;
- generic Commitment, Claim, Entity, Situation, ontology, or graph retrieval;
- semantic rediscovery across arbitrary life data;
- preference learning or generalized proactivity;
- multi-agent execution, iOS/offline continuity, or generalized-assistant behavior;
- real-world booking or healthcare-provider confirmation.

No roadmap or product narrative should claim those are validated by this experiment.

## 5. Phase -1: Product Reality Study

**PHASE -1:** Before active personal-source ingestion or significant Graph implementation, determine whether appointment continuity is a recurring problem rather than a convenient engineering slice.

With explicit consent, manually compare a bounded corpus of appointment-related messages with corresponding calendar state. Do not deploy a continuous connector or retain credentials in Tessera.

Measure:

- cases already represented correctly in the calendar;
- useful information missing from calendar state;
- stale state after reschedule/cancellation;
- multi-message cases needed to establish current state;
- historical-search and manual-reconstruction frequency;
- cases where prior correction/context changes interpretation;
- error/manual-work reduction opportunity;
- ICS versus unstructured messages;
- expected manual-review and selected-folder routing burden.

Required outputs:

1. bounded, consented, redacted corpus;
2. workflow-frequency and routing-burden statistics;
3. evidence of user pain and continuity value;
4. examples already solved by existing calendar behavior;
5. baseline manual reconstruction/repeated-input measures;
6. `GO`, `PIVOT`, or `STOP` recommendation.

Reconsider the wedge when errors are rare, calendar behavior solves most cases, routing/manual entry dominates, history is rarely revisited, or prior state rarely changes the result. Only a documented `GO` permits implementation.

## 6. MVP: Read-only appointment continuity

### Product statement

> Tessera notices an appointment confirmation, reschedule, or cancellation in a user-selected Outlook source, preserves the accepted current and prior state, shows the evidence supporting each consequential field, and lets the user correct the interpretation.

### User job

> When appointment details change across email, keep the correct current state visible and let me understand what changed and why without manually reconstructing the thread.

### Target deployment

> **Single-user, user-operated Tessera server with Microsoft cloud dependencies.**

| Capability | User-controlled | External dependency |
|---|---:|---|
| Tessera web/API | Yes | None |
| Product database/backup volume | Yes | None |
| Mail/calendar | No | Microsoft Graph |
| Identity | No | Microsoft Entra |
| Secret custody | No | Azure Key Vault |
| Extraction | Yes | Deterministic/manual or approved local model |

This is not a self-contained local system. The gap from the trusted-edge/local-first direction is explicit architecture debt.

### Selected-folder limitation

The selected folder is a privacy boundary and a product-proof limitation. MVP demonstrates that Tessera understands explicitly routed information, not that it discovers what matters across a mailbox.

Measure manual routing count/time, missed relevant messages, irrelevant routed messages, and whether routing burden declines. Treat this as pilot selection bias; do not widen ingestion until value and missed-value analysis justify it.

### Included capabilities

- separate Graph source authorization with `Mail.Read`, `Calendars.ReadBasic`, and `offline_access` only;
- start-now, selected-folder delta ingestion and selected-calendar basic read;
- bounded ICS-only attachment path and deterministic parsing;
- manual or approved local-model path for unstructured messages;
- candidate review, field-level provenance, and reversible correlation;
- Appointment, AppointmentRevision, and Correction persistence;
- current/history, field-level Why, correction, forget, and recovery;
- Attention, Tracked, Appointment Detail, Connections, and Settings surfaces.

### Non-goals

- calendar writes or any external mutation;
- cloud LLM extraction;
- generic Commitment, Claim, Entity, Situation, Goal, or Preference types;
- semantic/vector retrieval or generalized proactive triggers;
- email drafting/sending or third-party booking;
- additional mail/calendar providers, browser automation, historical import, or arbitrary mailbox search;
- general chat/voice, iOS/offline/sync, enterprise administration, or autonomous decisions.

## 7. MVP product records

The MVP uses workflow-specific records. It does not introduce a generic personal-world schema.

### 7.1 EvidenceRecord

```yaml
evidence_id:
owner_principal:
source_account:
source_type:
source_native_id:
folder_id:
source_locator:
content_hash:
captured_at:
source_timestamp:
bounded_excerpt:
sensitivity:
retention_state:
extractor_version:
schema_version:
```

Evidence references identify the supporting fragment, not merely the message. Raw bodies are transient. Persist at most 2,048 Unicode characters across fragments needed to explain accepted fields.

### 7.2 Appointment

```yaml
appointment_id:
owner_principal:
lifecycle_state:
current_revision_id:
provider_correlation_key:
created_at:
updated_at:
```

### 7.3 AppointmentRevision and field provenance

Every consequential field (`status`, user-visible `title`, `provider`, `clinician`, `location`, `start`, `end`, and `time_zone`) stores:

```yaml
value:
assertion_type:
evidence_refs:
confidence:
accepted_at:
```

The revision also stores its ID, appointment ID, sequence, effective/superseded timestamps, extractor version, and schema version. Cancellation is a status value with its own provenance.

The MVP does not need a generic Claim object, but deliberately implements claim semantics at field level.

### 7.4 Correction

```yaml
correction_id:
appointment_id:
revision_id:
field:
old_value:
new_value:
user_principal:
created_at:
evidence_id:
```

A correction is immutable user-authored evidence. It supersedes a field, survives reprocessing, and remains traceable until forget.

### 7.5 Infrastructure records

Connector account, consent, selected source IDs, delta cursor, scheduler checkpoint, and erasure journal are infrastructure state, not product ontology. `ActionAttempt` is MVP+1, not MVP.

## 8. MVP storage, backup, and erasure

### 8.1 Product store

Use SQLite in WAL mode on an encrypted user-controlled volume. Require schema-version checks, forward migration tests, pre-migration backup, and restore verification. Do not add a second database or speculative portability layer.

### 8.2 Concrete backup strategy

The MVP selects a **short-lived encrypted backup window with a separate erasure journal**:

- SQLite Online Backup API creates one encrypted daily snapshot on a separate user-controlled backup volume.
- Retention is at most seven days.
- Each snapshot uses AES-256-GCM with a random data-encryption key wrapped by an Azure Key Vault key; raw data keys are never persisted.
- An append-only HMAC-keyed erasure journal lives on the backup volume outside database snapshots.
- Storage versioning, recycle bins, immutable retention, and WORM behavior are disabled for this pilot location.
- Snapshot IDs are enumerable and deletion is verified against manifest and filesystem.

Forget deletes live excerpts, appointments, revisions, corrections, and user-facing receipts, then appends a keyed tombstone. The UI reports `erasure pending` until all pre-deletion snapshots expire or are removed and inventory is verified; it then creates a post-deletion snapshot and reports `erasure complete`.

Restore tooling replays the external erasure journal before serving traffic. Missing/unverifiable journal means restore fails closed.

The guarantee is logical erasure from the configured backup set within seven days, not certified forensic sector erasure. Phase -1 uses no automated Tessera backup.

### 8.3 Export

Export contains current appointments, revision history, field provenance, retained evidence fragments, corrections, and connector metadata. It excludes credentials and security-audit rows.

## 9. MVP source and extraction boundary

### 9.1 Authorization and revocation

Source connection is separate authorization-code plus PKCE with a server-generated Key Vault reference. Access is limited to `Mail.Read`, `Calendars.ReadBasic`, `offline_access`, and signed-in user identity. There is no mail-send or calendar-write permission.

Microsoft permissions remain mailbox/calendar-wide; Tessera enforces selected IDs. Consent copy states that limitation.

- Internal disable stops Tessera use but does not change Microsoft consent.
- Disconnect deletes local Graph credentials but does not claim provider revocation.
- Provider revocation is verified through refresh failure and local cleanup.
- `invalid_grant` stops sync, marks revoked, deletes unusable local material, and requests reconnect.

### 9.2 Delta ingestion

- Store folder/calendar selection by immutable Graph ID and construct paths server-side.
- Use a start-now baseline; create no evidence from history.
- Commit each page before advancing its delta link.
- Expired/invalid delta state produces a visible sync gap and start-now rebaseline.
- Honor `Retry-After`, bounded backoff, and durable next-attempt time.
- Folder deletion requires reselection; rename is harmless.
- Fetch only post-baseline selected-folder messages; never fetch remote resources.
- Sanitize HTML to plain text and cap normalized input at 64 KiB.

### 9.3 Attachment policy

Only MIME-validated `text/calendar`/`.ics` is downloaded, with a 256 KiB decoded cap. There is no recursive extraction, arbitrary document parsing, or remote-resource fetch. Parse only workflow fields and treat textual values as untrusted.

Malformed, oversized, or spoofed ICS enters manual review. Other attachments are not downloaded or parsed; the UI may show `unsupported attachment present`.

### 9.4 Extraction and correlation

All email content remains local. No raw or sanitized body is sent to a remote LLM.

1. Parse allowed ICS deterministically.
2. Apply deterministic normalization.
3. Create manual-review candidates for unstructured messages.
4. An explicitly approved local model MAY fill the same versioned candidate schema.
5. Treat every field as candidate until accepted.
6. Auto-link only on an explicit reference ID or reviewed provider thread.
7. Otherwise ask new, existing, or uncertain.
8. Support auditable unlink/relink; title/person/time similarity alone never merges.

Email content supplies data only, never policy, permissions, tools, or approval. A remote extractor requires a future product/security decision.

## 10. MVP experience

- **Attention:** candidates, uncertain links, unsupported items, and sync gaps.
- **Tracked:** accepted current appointment state.
- **Appointment detail:** current fields, history, field-level Why, correction, and forget.
- **Connections:** source consent, selected resources, health, and revocation.
- **Settings:** retention, backup/erasure status, export, and account controls.

The current portal moves to supporting operations/settings. Storybook defines loading, empty, candidate, uncertain-link, corrected, source-unavailable, revoked, sync-gap, unsupported-attachment, forgotten, and erasure-pending states before implementation.

## 11. MVP evaluation and product metrics

### 11.1 Engineering corpus

Use redacted/synthetic development data and a sender/thread-separated frozen holdout. Target at least 120 development and 80 holdout items when practical. If smaller, report thresholds as engineering gates, not statistically strong real-world claims. No sender/template/thread family appears in both sets.

| Dimension | Measures | Engineering gate |
|---|---|---|
| Detection | precision, recall, per-class recall | >=95% precision, >=90% recall, no positive class below 85% recall |
| Field extraction | exact match, per-field error | >=95% exact consequential-field match before correction |
| Correlation | merge precision, false merge/split | zero false merges on holdout; false split <=5% |
| Temporal state | stale-state, supersession, cancellation | >=98% current-state; 100% cancellation accuracy on holdout |
| Provenance | coverage, incorrect support | 100% field coverage; zero unsupported provenance |
| Correction | persistence, reprocessing regression | 100% persistence; zero regressions |

### 11.2 Product and compounding metrics

Track stale/conflicting states found, manual reconstruction avoided, time to answer "what changed?", routing burden/misses, timeline value, and cases where prior state improves the result.

Compounding metrics MUST isolate Tessera-owned persisted state: correction rate, repeated-input burden, manual-correlation rate, prior-state utilization, rediscovery success, and stale/duplicate/repeated-input prevention over sequential cohorts.

The product gate requires at least five reviewed cases using prior accepted state and three cases where that state avoids repeated input, stale understanding, or manual reconstruction.

### 11.3 Read-only pilot exit

Elapsed time alone is insufficient. Real and staged coverage includes at least 10 confirmations, 5 reschedules, 5 cancellations, 5 ambiguous new/update decisions, 5 corrections, 3 connector restart/recovery cases, 5 prior-state uses, and 3 demonstrated compounding benefits. Product conclusions distinguish real from staged cases.

### 11.4 Mandatory STOP/PIVOT/CONTINUE review

MVP completion ends in a product-owner decision. Continue only when the workflow recurs, history/Why are used, correction and routing burden are acceptable, prior state prevents real work/errors, and execution would materially improve value.

Stop or pivot when value is rare, ICS/calendar solves most cases, manual review dominates, history is unused, the experience feels like data entry, or persisted context does not reduce effort.

## 12. MVP+1: Calendar reconciliation

### 12.1 Scope and ownership

After an explicit `CONTINUE TO EXECUTION`, acquire incremental `Calendars.ReadWrite` consent. Event classes are `TESSERA_MANAGED`, `TESSERA_ADOPTED`, `EXTERNAL_UNMANAGED`, and `USER_MANAGED`. Tessera mutates only managed/adopted events.

Adoption records event ID, ETag, prior state, user approval, and appointment ID. Similarity never implies adoption. Manual edits create a conflict and require a new decision.

Cancellation defaults to preserving history: propose a canceled, non-blocking/free representation for managed/adopted events. Destructive delete is a separate explicit action. Unmanaged events are never changed.

### 12.2 Action states

```text
PROPOSED
AUTHORIZED
STARTED
EXECUTION_SUCCEEDED
PROVIDER_VERIFIED
EXTERNALLY_CONFIRMED
FAILED
CANCELED
```

`EXECUTION_SUCCEEDED` means Graph accepted the mutation. `PROVIDER_VERIFIED` means a fresh Graph read matches provider state. `EXTERNALLY_CONFIRMED` requires independent clinic/booking/user evidence. Generic `verified` is forbidden.

### 12.3 ActionAttempt and resilience

ActionAttempt stores appointment/event IDs, ownership class, exact payload hash, policy, state, transaction ID, ETag, provider summary/timestamps, external evidence, failure class, and reconciliation state.

- Natural key: principal, calendar ID, appointment ID.
- Create uses stable transaction ID and remote marker.
- One equivalent marker reconciles; multiple are integrity failure; one non-equivalent marker is conflict.
- Update rereads and uses `If-Match`; `412` requires review/new approval.
- Timeouts enter durable reconciliation, never blind retry.
- Cancellation update is provider-verified by reread.
- Explicit deletion is verified by absence of ID and marker.

Before write pilot: at least 10 creates, 5 reschedule updates, 5 cancellation-preserving updates, 3 timeout reconciliations, 3 ETag conflicts, 3 adoptions, and approval replay/payload-swap/cross-user attacks. Duplicate writes and unmanaged mutations must be zero; approval compliance and provider verification must be 100%.

MVP+1 proves provider state, not real-world booking.

## 13. Current implementation audit

### 13.1 What actually ships

| Area | Repository truth | Reuse decision |
|---|---|---|
| Identity | Entra access-token validation for user and app-only callers; no production mTLS/SVID host authentication | Keep OIDC; correct claims and docs |
| Policy | Default-deny grants by caller, end user, target, and action; read/use/manage planes | Keep |
| Credential custody | In-memory or Azure Key Vault bundles with bearer/cookie/API-key/Basic injection | Keep |
| Egress | Host allow-list, address checks, no redirects/proxy/ambient cookies, injected credentials | Keep |
| Raw proxy approval | Content-bound, principal-bound, single-use portal challenge | Generalize and keep |
| Named provider tools | Generic recipe calls; caller-supplied `confirm` boolean permits step-up execution | Block writes until unified approval |
| Session health | Use-based liveness, refresh orchestration, single-process lease | Keep as connector operations |
| Audit | Decision-oriented JSONL plus an in-memory UI tail | Extend with durable action outcomes |
| Portal | Account health, connection flow, activity, pending writes, and several placeholder admin routes | Reuse shell/components; redesign product IA |
| Personal continuity | No evidence ledger, appointment state/history, field provenance, correction, deletion, or product database | Build only after Phase -1 and Phase 0 |

### 13.2 Blocking findings

#### B1. Named-tool writes can self-confirm

The generic provider path accepts a caller-controlled `confirm` boolean and proceeds when it is true ([ProviderEgress.cs](../src/Tessera.Providers/ProviderEgress.cs#L114-L155), [TesseraMcpTools.cs](../src/Tessera.Mcp/TesseraMcpTools.cs#L53-L68), [CallerBrokerEndpoint.cs](../src/Tessera.Broker/CallerBrokerEndpoint.cs#L82-L107)). A prompt-injected or compromised caller can set that value.

The raw proxy path has the stronger design: it issues a content-bound challenge and requires approval in the portal before consuming it once ([EgressProxyEndpoint.cs](../src/Tessera.Broker/EgressProxyEndpoint.cs#L202-L286)). The stronger path must become the only external-write authorization mechanism.

#### B2. A portal user chooses the credential-store key

`POST /portal/connections` accepts `credential` from the browser and a member may submit it for their own principal ([PortalEndpoints.cs](../src/Tessera.Broker/PortalEndpoints.cs#L390-L417)). `AddConnectionAsync` writes that value directly into a binding ([PortalService.cs](../src/Tessera.Core/Portal/PortalService.cs#L422-L463)). If store key names are guessable, a user can attempt to bind another stored credential to their identity.

The product must use a server-generated, owner-bound credential reference created by the connector flow. Users must never name arbitrary vault entries.

#### B3. Identity keys are not canonical enough for personal state

Bindings match either the token's `oid` or case-insensitive `preferred_username` ([TargetBinding.cs](../src/Tessera.Core/Resolution/TargetBinding.cs#L36-L57)). Audit and portal scoping prefer the human-readable username. Email addresses can change or be reused, and an object ID is not globally unique without issuer/tenant context.

Canonical product ownership must use an immutable composite such as `(issuer, tenant, subject)`. Display email remains an attribute, never the authorization key.

#### B4. Portal-added bindings do not update the broker resolver

The host constructs `CredentialResolver` once from startup bindings ([BrokerHost.cs](../src/Tessera.Broker/BrokerHost.cs#L101-L107)). `PortalService` replaces its own policy snapshot after a connection is added, but the singleton resolver retains its original binding array ([PortalService.cs](../src/Tessera.Core/Portal/PortalService.cs#L422-L463)). The refresher reads `CurrentPolicy`, while normal broker egress still uses the stale resolver.

A connection created in the portal can appear in the UI but remain unusable until restart. Policy and binding consumers need one atomic current snapshot.

#### B5. Microsoft Graph examples are not a working connector

The Graph recipes are configuration examples with shape and policy tests. They do not provide a complete Graph authorization flow, usable OAuth refresh request, delta ingestion, result normalization, or calendar mutation.

- Generic refresh posts an empty body to the token endpoint ([SessionRefresher.cs](../src/Tessera.Providers/SessionRefresher.cs#L75-L105)); a Graph refresh requires form parameters including grant type, refresh token, and client identity.
- The `calendarView` example declares no allowed date-range query parameters.
- Metadata tools return a truncated raw upstream body rather than parsed metadata items and opaque handles ([ProviderEgress.cs](../src/Tessera.Providers/ProviderEgress.cs#L247-L266)).
- `ResultEnvelope` and `MutationReceipt` exist only as unused domain types ([ResultEnvelope.cs](../src/Tessera.Core/Results/ResultEnvelope.cs#L105-L140)).
- The example grants calendar read only and defines no calendar create/update path ([grants.connectors.example.json](../deploy/config/grants.connectors.example.json#L32-L57)).

#### B6. There is no durable product or action state

The repository has no application database, evidence records, appointment state, correction history, action state machine, or provider receipt store. Audit records authorization and optional credential status, not product state or outcomes ([IAuditSink.cs](../src/Tessera.Core/Audit/IAuditSink.cs#L20-L58)).

In-memory consent, health, approval, OAuth-pending, and audit-tail state is acceptable for broker soft state. It cannot support the product specification's cross-session continuity or long-running user approvals.

#### B7. Privacy lifecycle is absent

There is no evidence retention policy, forget/delete workflow, tombstone behavior, backup erasure contract, or derived-state rebuild. This is a development-order blocker before active personal-mail ingestion.

### 13.3 High-priority findings

#### H1. Workload mTLS/SVID is documented but not hosted

The model contains mTLS/SVID verification enums and the documentation presents it as shipped, but `BuildValidator` only constructs an Entra OIDC validator; every other identity mode gets a deny-all validator ([BrokerHost.cs](../src/Tessera.Broker/BrokerHost.cs#L325-L343)). Kestrel is bound as plain HTTP in the host. Documentation should distinguish implemented OIDC from planned workload authentication.

#### H2. Provider output classes are labels and byte caps, not data boundaries

The code enforces a handle-shaped input for full-body tools and smaller byte caps for metadata, which is useful. It does not parse provider JSON, issue opaque handles from list results, remove body fields, sanitize snippets, or return receipts. A raw provider response can therefore violate the stated class while still carrying the `Metadata` label.

Provider-specific adapters must own normalization. Generic byte truncation is not spill control.

#### H3. Actions are authorized but not verified

The broker distinguishes policy effects and can report transport responses, but it lacks a durable lifecycle from proposal through provider verification. A timeout after provider success can be retried as a duplicate. This is incompatible with the v0.9 action invariants and blocks calendar writes.

#### H4. Live-view messages are not origin-bound

The iframe message handler accepts any window message whose payload parses; it checks neither `event.origin` nor `event.source` ([LiveViewIframe.tsx](../web/src/components/handoff/LiveViewIframe.tsx#L22-L33)). A live-view completion signal must be accepted only from the armed iframe's expected origin and window.

### 13.4 Development-order constraint

> **No personal-source ingestion or write-capable connector implementation may be merged into the active product path while Phase 0 trust-boundary blockers remain unresolved.**

Phase 0 exit requires:

- no caller-controlled self-confirm path;
- server-generated, owner-bound credential references only;
- canonical principal ID `(issuer, tenant, subject)`;
- one atomic live policy/binding snapshot;
- origin-bound iframe messages;
- authorization regression tests;
- cross-principal credential-binding tests.

Phase -1 manual research is allowed because it does not add an active connector.

### 13.5 Product and documentation findings

#### P1. The shipped UI is an operator console, not the proposed product

The app defaults to `/accounts` and centers connection health ([App.tsx](../web/src/App.tsx#L54-L82)). It has no appointments, evidence timeline, correction, or "Why?" experience. `/action-required`, cross-person detail, and all-connections are unfinished/placeholder surfaces.

Keep the operations tools, but do not use them as the primary personal-continuity UX.

#### P2. Repository claims overstate several implementation surfaces

The README describes a bundled browser harvester, workload mTLS/SVID, per-tenant envelope encryption, shaped personal-data results, and a broadly completed action broker. The source tree contains no worker project, the host lacks mTLS/SVID authentication, envelope keys are deferred, and result shaping is not on the production path.

The roadmap also opens with "one iteration" in which nothing load-bearing is deferred, then records multiple load-bearing deferrals. The UI spec still says "design only" despite a shipped React application. These documents need one current capability matrix after MVP sign-off.

#### P3. Tests prove components, not the product promise

The repository has substantial deterministic unit and endpoint coverage around policy, identity, egress, OAuth-MCP, health, portal behavior, and config examples. It does not have a real Graph integration test, a mailbox/calendar workflow, model-adversarial extraction tests, durable restart tests, duplicate-action reconciliation, or deletion tests.

## 14. Specification audit

### 14.1 What v0.9 gets right

- The user, not an agent, is the center.
- Evidence is distinct from claims and current state.
- Current state is distinct from history.
- Models are replaceable workers.
- Policy is deterministic and outside the model.
- Execution success is distinct from outcome verification.
- Corrections and supersession are durable.
- Long-running work lives outside prompts.
- Embeddings are an index, not truth.
- Ontology and generalized autonomy are not the user experience.

These are good long-term invariants and should survive the MVP reduction.

### 14.2 What v0.9 should change

1. Split **Vision**, **MVP**, and **Architecture Options** into separate documents. Normative requirements and provisional ideas are currently interleaved.
2. Replace the 20-item Core MVP with the single workflow in this document.
3. Do not make email draft/send, hybrid retrieval, persistent generic situations, preferences, and broad entity extraction MVP requirements.
4. Treat Regina Maria as a later vertical acceptance test. It is not the first product slice because it adds health sensitivity and provider-specific execution before continuity is proven.
5. Resolve the deployment product first. Local-first iOS, encrypted cloud state, server-side self-hosting, and sync are materially different architectures and cannot all remain implicit MVP possibilities.
6. Define evidence retention before source ingestion, not after it.
7. Replace the initial generic core with Appointment-specific records and field provenance. Introduce generic Commitment, Claim, Entity, and Situation only when measured workflow failures justify them.
8. Add an explicit migration statement: the existing broker becomes the trust/execution module inside the new product; it is not the canonical knowledge store.

### 14.3 Decisions to retain

- .NET modular monolith for the server.
- Identity-first, fail-closed authorization.
- Separate caller and end-user identity.
- Pluggable credential store and secret injection.
- File-reviewable grants and deterministic action policy.
- Action planes and credential ownership.
- SSRF-hardened egress.
- Out-of-band, content-bound write approval.
- Use-based connection health.
- Secret-free security audit.
- Single-writer session rotation for the current topology.

### 14.4 Decisions to supersede or narrow

- Supersede "credential broker" as the complete product definition; retain it as a module.
- Supersede the current one-iteration roadmap with workflow milestones.
- Narrow the admin portal to supporting operations/settings.
- Narrow provider recipes to transport declarations; provider adapters own typed data normalization and action verification.
- Replace username-or-object-ID ownership matching with canonical principal IDs.
- Replace every caller-controlled write confirmation with one challenge/approval contract.
- Defer worker grids, generalized browser automation, rich ontology, semantic index, and iOS sync.

## 15. Delivery plan

All implementation phases are unstarted. Completion requires executable gates and review artifacts, not merged code alone.

| Phase | Deliverable | Exit |
|---|---|---|
| **-1. Product Reality Study** | Corpus, pain/frequency, routing burden, continuity examples | `GO`, `PIVOT`, or `STOP` |
| **0. Trust reset** | Fix approval, credential refs, canonical identity, live binding snapshot, iframe origin | No unresolved trust blockers |
| **1. Durable read-only core** | EvidenceRecord, Appointment, AppointmentRevision, Correction, SQLite, backup/erasure, API | Restart, provenance, supersession, correction, forget, restore tests pass |
| **2. Microsoft read connector** | Selected-folder mail, ICS path, calendar basic read, delta/revocation/recovery | Consent, refresh, delta, attachment, revocation, boundary tests pass |
| **3. Read-only MVP** | Candidate review, Tracked, Detail, field Why, correction, compounding metrics | Engineering, product, continuity, trust, event-count gates pass |
| **3.5 Product Review** | Review Phase -1/MVP evidence | Explicit `STOP`, `PIVOT`, or `CONTINUE TO EXECUTION` |
| **4. Calendar execution backend** | Incremental write consent, adoption, approval, idempotency, provider verification | Sandbox mutation/adversarial gates pass |
| **5. Execution UI** | Proposal, adoption, conflict, provider/external confirmation UX | Accessibility/action-state gates pass |
| **6. Execution pilot** | Event-count-based write pilot | Safety, coverage, and product-value gates pass |

## 16. Required test matrix

### MVP

- confirmation, reschedule, cancellation, tentative-to-confirmed, contradictory follow-up;
- duplicate message/page, expired cursor, throttling, concurrent poll, folder rename/deletion;
- similar unrelated appointments, ambiguous new/update, false merge/split, unlink/relink;
- time zones and daylight-saving transitions;
- hostile subject/HTML and ICS title/location/description;
- malformed/oversized/spoofed ICS and unsupported attachments ignored;
- scripts, remote images, tracking links, hidden text, oversized bodies;
- correction followed by deterministic/local-model reprocessing;
- source deletion, revocation, forget, erasure pending/completion, restore;
- restart during sync/review;
- cross-principal credential/source attacks;
- field provenance deletion/supersession;
- no remote model/network call from extraction.

### MVP+1

- approval replay, payload swap, and cross-user approval;
- create timeout/marker reconciliation and multiple/non-equivalent marker conflict;
- ETag conflict after direct edit;
- cancellation-preserving update and explicit delete;
- all four ownership classes;
- separate execution success, provider verification, and external confirmation.

## 17. Earned structure gates

- Add generic `Commitment` only when a future-oriented obligation workflow cannot be represented as Appointment.
- Add `Situation` when a recurring user-visible context groups multiple records/actions.
- Add generic `Claim` when field-level revisions fail measured conflicts.
- Add `Entity` when provider-native identities fail a measured workflow.
- Add semantic retrieval when structured current/history queries fail recurring questions.
- Add a second provider only after the Graph read contract stabilizes.
- Add cloud extraction only through a separate product/security decision.
- Add iOS/offline/sync only after web continuity demonstrates recurring value.

## 18. Sign-off decisions

This document becomes controlling product scope only after explicit approval that:

1. Appointment continuity remains the first experiment only after Phase -1 evidence.
2. Read-only continuity is MVP.
3. Calendar write is MVP+1 and requires a separate continue decision.
4. Selected-folder ingestion is an accepted pilot limitation.
5. The MVP record is Appointment, not generic Commitment.
6. Field-level provenance is mandatory.
7. No cloud extraction occurs in MVP.
8. The seven-day encrypted rolling backup plus external erasure journal and logical-erasure semantics are accepted.
9. The deployment is user-operated with Microsoft cloud dependencies.
10. Pilot exit requires event coverage, not elapsed time.
11. Evaluation separates detection, extraction, correlation, temporal state, provenance, correction, and product value.
12. Phase 0 trust fixes precede active personal-source ingestion.
13. Provider verification and real-world confirmation are separate.
14. Ownership, adoption, conflict, cancellation-preservation, and explicit-delete semantics are accepted.
15. The ICS-only attachment policy is accepted.
16. Model reviews are design inputs, not validation.
17. "Structure is earned" is an architecture invariant.
18. Compounding-memory metrics are required.
19. The MVP hypothesis boundary will not be overclaimed.
20. An explicit `STOP`, `PIVOT`, or `CONTINUE TO EXECUTION` decision follows MVP.

After approval, this document controls the experiment. The v0.9 specification remains a long-term vision and must link back to this narrower scope.