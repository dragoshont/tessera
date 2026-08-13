# R2 Realtime Voice API Contract

**Status:** Accepted additive `/api/v1` contract

This document extends `R2_API_CONTRACT.md`. Its common ID, timestamp, Problem
Details, owner isolation, authentication, strict JSON, idempotency, redaction,
and pagination rules apply unchanged.

## Wire types

```text
RealtimeVoiceStatusDto {
  state: CHECKING|READY|BLOCKED|UNAVAILABLE,
  blockedCode: string|null,
  supportsTools: boolean,
  maxSessionSeconds: integer,
  checkedAt: timestamp|null,
  validUntil: timestamp|null,
  version: integer
}

RealtimeNegotiationDto {
  sessionId: string,
  answerSdp: string,
  negotiatedAt: timestamp,
  expiresAt: timestamp,
  maxSessionSeconds: integer
}

RealtimeTurnInput {
  clientTurnId: string,
  inputItemId: string,
  outputItemId: string|null,
  userTranscript: string,
  assistantTranscript: string|null,
  assistantDisposition: COMPLETED|INTERRUPTED|FAILED
}

RealtimeTurnReceiptDto {
  sessionId: string,
  clientTurnId: string,
  userMessage: MessageDto,
  assistantMessage: MessageDto|null,
  replayed: boolean
}

RealtimeToolCallInput {
  clientCallId: string,
  name: string,
  arguments: object
}

RealtimeToolCallDto {
  sessionId: string,
  clientCallId: string,
  state: COMPLETED|APPROVAL_REQUIRED|FAILED,
  capabilityCallId: string|null,
  capabilityResultId: string|null,
  actionId: string|null,
  output: object|null,
  errorCode: string|null
}

RealtimeEndInput { reason: USER_ENDED|CONVERSATION_CHANGED|SIGNED_OUT|PAGE_CLOSED|APP_BACKGROUNDED|INTERRUPTED|EXPIRED|ERROR }
```

`blockedCode`, `errorCode`, and end reasons are closed server enums even where
shown as strings. Transcript and tool IDs are 1-128 visible ASCII characters.
Transcript strings are normalized UTF-8 text, each limited to 32 KiB; combined
turn text is limited to 48 KiB. Tool names and arguments use existing plugin
schema and coordinator limits. `output` is the existing normalized, bounded,
secret-redacted `CapabilityResult` public shape, never a raw provider body.

`answerSdp` is the only provider signaling content returned. The DTO never
contains a provider URL, resource name, deployment name, model ID/version,
standing credential, ephemeral secret, call Location header, ICE server
credential, instructions, hidden context, or tool authorization.
It necessarily contains media-plane ICE candidates and a DTLS fingerprint for
the client's direct Foundry peer connection. Clients keep that SDP ephemeral and
never persist or log it.

## Operations

| Operation | Request | Success | Preconditions and stable failures |
|---|---|---|---|
| `GET /realtime-voice/status` | none | `200 RealtimeVoiceStatusDto` | Authenticated owner; cached metadata-only readiness projection; never initiates a probe |
| `POST /conversations/{id}/realtime-sessions` | `{ clientAttemptId, offerSdp }` + `Idempotency-Key` | `201 RealtimeNegotiationDto` | ACTIVE owner conversation; exact current deployment ready; `415 invalid_media_type`, `422 realtime_unavailable/realtime_offer_invalid`, `429 realtime_session_limit`, mapped provider failures |
| `POST /conversations/{id}/realtime-sessions/{sessionId}/turns` | `RealtimeTurnInput` + `Idempotency-Key` | `201 RealtimeTurnReceiptDto` | Session/conversation/owner binding; exact replay or `409 idempotency_conflict/realtime_turn_conflict` |
| `POST /conversations/{id}/realtime-sessions/{sessionId}/tool-calls` | `RealtimeToolCallInput` + `Idempotency-Key` | `200|202 RealtimeToolCallDto` | Canonical availability and grants; `202` only for `APPROVAL_REQUIRED`; existing Action errors apply |
| `POST /conversations/{id}/realtime-sessions/{sessionId}/end` | `RealtimeEndInput` + `Idempotency-Key` | `200 MutationReceipt` | Metadata-only idempotent close; already terminal returns exact receipt |

All routes derive owner from the authenticated principal. Other-owner
conversation/session/turn/tool identifiers return indistinguishable `404`.

## Negotiation contract

The client sends JSON because the owner-scoped attempt ID and SDP must bind in one
strict request. `offerSdp` is treated as opaque signaling, limited to 64 KiB UTF-8,
must contain no NUL/control characters other than SDP line separators, and is
never persisted or logged. It is forwarded only as `Content-Type:
application/sdp` to the fixed configured Foundry calls endpoint. Redirects are
disabled. Foundry's answer is subject to the same size/text bounds.

`clientAttemptId` and `Idempotency-Key` identify one negotiation attempt. Tessera
serializes duplicate attempts while in flight. An exact replay can return the
original answer only while its short in-memory replay entry remains valid. Since
SDP and ICE credentials are never durably persisted, a replay after that entry is
gone returns `409 realtime_negotiation_expired`; the client must close the old
peer connection and explicitly create a fresh offer, attempt ID, and key. Tessera
never silently mints twice for an ambiguous attempt.

The canonical request hash binds owner, Conversation, `clientAttemptId`,
`Idempotency-Key`, and the exact offer hash. Reusing either identifier with a
different counterpart or offer returns `409 idempotency_conflict`; neither
identifier can be rebound to another negotiation.

Before the first upstream call, Tessera durably inserts `NEGOTIATING` with a
monotonic generation and `negotiationDeadline` no more than 30 seconds ahead. The
request owns that generation. A duplicate on the same process joins the
single-flight result until the deadline. Request cancellation, timeout, upstream
ambiguity, or process failure never transfers ownership or remints under the same
attempt. On startup and before handling a duplicate, an expired `NEGOTIATING` row
is atomically fenced to `FAILED` with `realtime_negotiation_outcome_unknown`; it
returns `409 realtime_negotiation_expired` and can never resume. If Foundry created
an orphaned call before the crash, the client never received its SDP answer, sends
no media to it, and the provider expires it. A retry always uses a new peer
connection, offer, attempt ID, and idempotency key.

The server performs this fixed upstream sequence inside the bounded request:

1. Resolve current canonical Conversation context, policy-filtered tools, and
   server-owned voice/session settings.
2. Authenticate to Foundry from approved custody/managed identity and call
   `/openai/v1/realtime/client_secrets` with model deployment
   `tessera-realtime-21`.
3. Read the ephemeral `value` without logging, persisting, tracing, or returning
   it, then immediately post `offerSdp` to `/openai/v1/realtime/calls`.
4. Discard references to the ephemeral value and upstream bodies after the call;
   retain only bounded metadata and the short in-memory answer replay entry.

The server does not consume the Foundry Location header and does not connect an
observer WebSocket. `NEGOTIATED` means only that Tessera issued an SDP answer;
clients establish and report their own peer/data-channel state.

## Readiness contract

`RealtimeReadinessService` is the sole probe owner. It runs at Broker startup,
after relevant server configuration changes, and when the prior result reaches
its five-minute `validUntil`, with per-installation single-flight and jitter to
avoid a probe stampede. It calls only the fixed GA client-secret endpoint using a
minimal session with no Conversation text, user instructions, or tools. Timeout
is 10 seconds. A valid ephemeral value with expiry at least 10 seconds ahead marks
`READY`; the secret is discarded and never used for SDP. `GET /status` only reads
the cached projection.

Missing configuration is `UNAVAILABLE`. Probe pending is `CHECKING`. Missing
deployment/model, 401/403, 429/quota, malformed value, timeout, or any stale result
is `BLOCKED` with a stable safe code and `validUntil=null`. A successful real
negotiation refreshes the five-minute result; any negotiation provider failure
invalidates it immediately. Probe and negotiation limits are separate so status
polling cannot consume provider quota.

`maxSessionSeconds` is server-owned configuration, defaults to 900 seconds, and
is clamped to 60-3600 seconds. Clients cannot extend it, and a shorter provider
expiry always wins.

## Transcript contract

The client submits only completed text events from its Foundry data channel. Deltas
remain local UI state. The server treats every transcript as untrusted user input:

- `userTranscript` creates one canonical USER Message and public text part.
- A nonempty `assistantTranscript` creates one canonical ASSISTANT Message linked
  by the realtime turn receipt. `INTERRUPTED` maps to `STOPPED`; `FAILED` maps to
  `FAILED`; otherwise it maps to `COMPLETED`.
- The pair, turn receipt, idempotency receipt, and public execution events commit
  atomically. No half-turn is visible.
- Empty/whitespace user text, unknown properties, invalid disposition, oversized
  text, control payloads, duplicate provider item binding, or changed replay fail
  closed.
- Client-reported transcript text is conversation history, not Evidence, Memory,
  provider verification, or Action authorization.

The API does not accept audio bytes, audio URLs, blobs, multipart media, base64
audio, waveform data, codec frames, or recording references on any voice route.

## Tool relay contract

Foundry emits a function call over the client data channel. The client forwards
only `{clientCallId,name,arguments}` to Tessera. Tessera resolves `name` from the
server-owned `realtime_session_tools` rows captured atomically before minting the
session secret. Each row binds exposed name, plugin/capability versions, selected
account, schema hash, and side-effect class. The same sorted rows generate the
Foundry session tool definitions. Tessera then re-runs current
`CapabilityAvailabilityService` immediately before dispatch; a missing or changed
binding fails closed rather than silently selecting another version/account.

- Unknown, disabled, changed-version, revoked-account, grant-mismatched, expired,
  or schema-invalid calls fail closed.
- Read/non-consequential calls execute through the existing coordinator and return
  a bounded canonical result for client relay to Foundry.
- Consequential calls create the canonical Action and return
  `APPROVAL_REQUIRED`; spoken assent and model claims never authorize it.
- Approval uses the existing `/actions/{id}/approve` route. Exact Action binding,
  one-use authorization, dispatch-time recheck, verification, and reconciliation
  remain unchanged.
- While the same client session is connected, its existing Conversation SSE can
  deliver the canonical capability result keyed by `capabilityCallId`; the client
  may then relay that result to Foundry. If the voice session ended, the result
  remains in canonical Chat/Activity and is not injected into a new session.
- A provider timeout or unknown consequential outcome is never blindly retried.

## Public events

The existing Conversation event stream adds these closed public types:

```text
realtime_negotiated  { sessionId, expiresAt }
realtime_ended       { sessionId, reason }
realtime_turn_saved  { sessionId, clientTurnId, userMessageId, assistantMessageId|null }
```

Existing `capability_requested`, `approval_required`, `capability_result`, and
`failure` events are reused. No SDP, token, network candidate, audio, transcript
delta, hidden instruction, provider event body, or Foundry call ID is emitted.

## Stable failure mapping

| Condition | HTTP / code |
|---|---|
| Feature not configured, deployment absent, or quota zero | `422 realtime_unavailable` with safe `blockedCode` |
| Invalid/oversized SDP | `422 realtime_offer_invalid` |
| Unsupported request content | `415 invalid_media_type` |
| Session/owner/conversation mismatch | `404 not_found` |
| Concurrent session or owner limit | `429 realtime_session_limit` |
| Foundry authentication/authorization rejected | `502 provider_auth_required` |
| Foundry quota/rate limit | `429 provider_rate_limited` |
| Foundry malformed/missing secret or SDP | `502 provider_malformed` |
| Foundry unavailable | `502 provider_unavailable` |
| Foundry timeout | `504 provider_timeout` |
| In-memory negotiation replay no longer available | `409 realtime_negotiation_expired` |
| Stale/crashed in-flight negotiation | `409 realtime_negotiation_expired` and durable `realtime_negotiation_outcome_unknown` |
| Transcript/tool changed replay | `409 idempotency_conflict` |
| Session terminal/expired | `409 realtime_session_ended` |

Problem detail never contains the upstream body, URL, key, token, deployment,
SDP, provider call ID, or transcript content.
