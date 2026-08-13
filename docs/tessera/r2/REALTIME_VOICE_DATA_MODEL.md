# R2 Realtime Voice Data And Migration Contract

**Status:** Accepted additive migration `v16`

## Principle

Voice persists canonical text and bounded metadata, never media. Existing
`conversations`, `messages`, `message_parts`, `capability_calls`,
`capability_results`, `actions`, `execution_events`, and `idempotency_receipts`
remain authoritative. Voice adds binding receipts; it does not add a second Chat,
tool, approval, or Job aggregate.

## Migration `v16`

Add these owner-scoped tables and indexes in one idempotent forward migration:

```text
realtime_session_receipts
  (ownerPrincipalId, id, conversationId, clientAttemptId,
  idempotencyKeyHash, offerHash, state,
  negotiationGeneration, negotiationDeadline,
   providerModelId, providerModelVersion, providerDeploymentRef,
   negotiatedAt, expiresAt, endedAt, endReason, failureCode, version)

realtime_session_tools
  (ownerPrincipalId, sessionId, exposedName, pluginId, pluginVersion,
  capabilityId, capabilityVersion, accountId, schemaHash, sideEffectClass)

realtime_turn_receipts
  (ownerPrincipalId, sessionId, clientTurnId, inputItemId, outputItemId,
   userMessageId, assistantMessageId, assistantDisposition, createdAt)

realtime_tool_bindings
  (ownerPrincipalId, sessionId, clientCallId, capabilityCallId,
   capabilityResultId, actionId, state, createdAt, updatedAt, version)
```

`providerDeploymentRef` is a server-internal non-secret configuration reference,
not a client value or endpoint. Historical metadata may record model
`gpt-realtime-2.1` and version `2026-07-07`; public DTOs do not expose deployment
identity. Every foreign key includes owner and references the canonical aggregate.

Required uniqueness:

- session `(ownerPrincipalId,id)` and `(ownerPrincipalId,clientAttemptId)`;
- session tool `(ownerPrincipalId,sessionId,exposedName)` and exact canonical
  plugin/capability/account owner bindings;
- turn `(ownerPrincipalId,sessionId,clientTurnId)`;
- provider input item `(ownerPrincipalId,sessionId,inputItemId)`;
- non-null provider output item `(ownerPrincipalId,sessionId,outputItemId)`;
- tool `(ownerPrincipalId,sessionId,clientCallId)`;
- linked Message, CapabilityCall, CapabilityResult, and Action IDs remain
  owner-consistent.

## Forbidden persistence

No table, idempotency body, event, trace, log, diagnostic, attachment, backup, or
analytics record may contain:

- raw or encoded input/output audio, recordings, waveform/sample data, audio URLs,
  codecs, RTP/SRTP packets, or media blobs;
- SDP offers/answers, ICE usernames/passwords/candidates, DTLS fingerprints,
  Foundry Location/call IDs, observer URLs, or network addresses;
- Foundry API keys, Entra access tokens, ephemeral client secrets, secret-store
  values, or full provider response bodies;
- transcript deltas, hidden prompts/instructions, hidden context, or model
  reasoning.

Only completed bounded transcript text is stored in canonical Message text parts.
Turn receipts store provider/client opaque IDs solely for duplicate binding; those
IDs are bounded and never treated as authorization.

## State machines

| Aggregate | Legal transitions |
|---|---|
| Session receipt | `NEGOTIATING -> NEGOTIATED|FAILED`; `NEGOTIATED -> CLIENT_ENDED|EXPIRED|FAILED`; terminal states immutable |
| Tool binding | `REQUESTED -> RUNNING|APPROVAL_REQUIRED|FAILED`; `RUNNING -> COMPLETED|FAILED|RECONCILIATION_REQUIRED`; `APPROVAL_REQUIRED -> RUNNING|FAILED`; reconciliation follows canonical Action truth |

`NEGOTIATED` records that Broker returned an SDP answer. It does not claim the
client peer connected, the microphone worked, media flowed, or Foundry responded.
There is deliberately no durable `ACTIVE` state without an allowed independent
observer. Client UI connection states are local and ephemeral.

Every `NEGOTIATING` row has one generation owner and a deadline no more than 30
seconds after creation. Startup recovery and duplicate handling atomically move an
expired row to `FAILED/realtime_negotiation_outcome_unknown`; it is never leased,
resumed, or reminted. A fresh client attempt creates a new row and generation.

## Transactions and idempotency

- Negotiation inserts `NEGOTIATING` plus generation/deadline and all sorted
  `realtime_session_tools` rows before upstream calls, then moves to one terminal
  negotiation outcome. The rows used to construct Foundry tool definitions are
  therefore the exact rows used to resolve later calls. Raw request/response
  bodies and tool descriptions are never part of the row.
- A transcript request atomically inserts the turn receipt, canonical user and
  optional assistant Messages/parts, public events, and idempotency receipt.
- Exact transcript/tool replay returns the original canonical references. Changed
  payload under the same key or client ID returns conflict.
- A tool binding and canonical CapabilityCall/Action association commit together.
  Canonical Action remains the source of external-effect state.
- End is idempotent metadata. Failure to receive an end receipt does not imply
  media continues; clients own and close media resources locally.

## Retention and deletion

Realtime transcript Messages follow the Conversation's existing retention,
archive, delete, export, and owner isolation semantics. Binding receipts follow
their referenced Conversation retention and contain no media. Deleting a
Conversation logically removes it from product views under the existing contract;
this feature makes no stronger erasure or backup claim.

## Rollback

Before rolling a `v16` binary back:

1. Disable new realtime negotiation through server configuration.
2. Allow the short negotiation timeout to drain; clients close active voice
   sessions locally and continue with typed Chat.
3. Do not down-migrate and do not rewrite canonical Messages created from voice.
4. Retain the additive `v16` tables; older binaries ignore them.
5. Keep the existing `gpt-realtime-1.5` Azure deployment available during the
   rollout window, but select any provider rollback only through reviewed
   server-owned configuration/private GitOps.

Re-upgrade resumes with existing canonical transcripts and receipts. No audio
recovery is possible or claimed because no audio was stored.
