# R2 Realtime Voice Security Contract

**Status:** Proposed; zero raw-audio trust boundary

This contract extends `R2_SECURITY_MODEL.md`. Existing authentication, owner
isolation, custody, SSRF, prompt-injection, capability, Action authorization,
redaction, and secret-scan requirements remain mandatory.

## Trust-boundary invariant

The only media path is:

```text
Tessera client microphone -- SRTP/DTLS WebRTC --> Microsoft Foundry
Tessera client speaker   <-- SRTP/DTLS WebRTC --- Microsoft Foundry
```

Tessera Broker is on the HTTPS control path only:

```text
client -- auth + SDP offer --> Tessera -- ephemeral-secret mint + SDP offer --> Foundry
client <-- SDP answer ------- Tessera <-- SDP answer ------------------------- Foundry
client -- transcript/tool metadata --> Tessera
```

Any implementation that gives Broker an audio track, RTP/SRTP socket, TURN relay,
audio WebSocket, audio upload, observer/controller stream, recording, audio
attachment, waveform, or base64 audio field violates this contract.

## Credential boundary

- Provider authentication is server-owned and comes from the approved managed
  identity or credential custody seam. It is never accepted from a client.
- The resource host and GA paths are fixed trusted configuration. Clients cannot
  supply resource, region, endpoint, URL, model, version, deployment, voice,
  instruction, tool definition, or observer location.
- The Foundry ephemeral secret is read only inside the negotiation request and
  immediately consumed against `/openai/v1/realtime/calls`. It is never returned,
  persisted, cached durably, logged, traced, measured as a tag, or included in an
  exception.
- HTTP redirects are disabled for both Foundry calls. TLS validation is never
  bypassed. Upstream body logging is forbidden.
- The deployment name `tessera-realtime-21` is an internal configuration value;
  clients receive only capability status and session metadata.

## SDP handling

SDP is signaling, not raw audio, but it contains short-lived ICE/DTLS and possible
network metadata. Therefore:

- request and response bodies are each limited to 64 KiB normalized UTF-8;
- unknown JSON properties, NULs, invalid control characters, and non-string SDP
  fail before an upstream call;
- reverse-proxy, ASP.NET, tracing, exception, and audit logging record only route,
  owner-safe session ID, status, duration, byte count, and stable failure code;
- no SDP body or candidate/fingerprint extraction is persisted;
- the answer is retained only in a bounded in-memory replay entry no longer than
  the ephemeral negotiation window;
- rate and concurrency limits apply per owner and globally to prevent quota abuse;
- the upstream destination is fixed, so opaque SDP cannot influence egress.

## Transcript integrity and privacy

Because Tessera intentionally has no observer connection, transcript text is an
authenticated client report, not independently attested Foundry output. It is:

- validated, bounded, normalized, owner/session/conversation-bound, and
  idempotently persisted;
- displayed as Conversation text and excluded from automatic Evidence/Memory,
  authorization, provider verification, or audit-proof claims;
- subject to existing Conversation access, export, retention, and deletion rules;
- redacted from ordinary logs and telemetry; metrics use counts and byte lengths,
  never content.

Clients render transcript text as text, never markup or executable content.
Prompt-like or tool-like transcript content cannot invoke a capability without a
separate structured tool event and server-side availability/schema checks.

## Tool and Action boundary

- The client data channel and model are untrusted. They cannot select an MCP URL,
  plugin version, account, credential, egress host, approval state, or success.
- Session tool definitions are derived server-side from current canonical
  availability and persisted as exact non-secret `realtime_session_tools`
  bindings before the Foundry session is minted.
- Every call is schema-validated and re-authorized immediately before dispatch.
- Spoken confirmation, transcript text, model claims, or client flags never
  authorize a consequential Action.
- Existing exact Action binding, one-use out-of-band approval, expiry, account and
  grant recheck, verification, unknown-outcome reconciliation, and no-blind-retry
  rules apply unchanged.
- Tool results are bounded and treated as untrusted content before the client
  relays them back over the Foundry data channel.

## Client controls

### Web

- Secure context and explicit user activation are required.
- Permission is requested only after the user selects Start voice.
- All local tracks are stopped on end, route teardown, sign-out, pagehide, error,
  or session expiry. Object references and audio element streams are cleared.
- Content Security Policy and connect permissions name Tessera and required
  Foundry/WebRTC destinations only; no wildcard provider egress is introduced.

### Electron

- Existing `nodeIntegration=false`, `contextIsolation=true`, `sandbox=true`,
  `webSecurity=true`, no-webview, pinned navigation, and narrow IPC remain.
- Main-process permission check/request handlers allow microphone media only for
  the exact main Tessera renderer and deny camera and every other permission.
- No new preload IPC exposes media, SDP, provider configuration, or credentials.
- The package carries `NSMicrophoneUsageDescription`; hardened runtime, fuse,
  signing/notarization, and package inspection gates remain.

### Expo iOS

- `NSMicrophoneUsageDescription` is mandatory and camera/background modes are
  absent. Permission denial/revocation fails closed.
- Authentication remains OIDC Authorization Code with PKCE; session material
  stays in Keychain. The WebRTC module never receives the OIDC refresh token.
- Audio session activation is scoped to an explicit foreground voice session and
  is deactivated on end/interruption/background.
- The native dependency must pass Expo SDK 57/RN 0.86/New Architecture and supply
  chain review. No unmaintained fork or private binary is accepted without a
  separate ADR and human approval.

## Telemetry and DLP

Allowed telemetry: stable session ID, owner-safe pseudonymous dimensions already
used by Tessera, state transition, duration, request/answer byte count, client
platform, stable error code, capability ID/version, and token usage if Foundry
provides bounded numeric usage through an allowed client event.

Forbidden telemetry: audio, SDP, transcript content, prompt/instructions, tool
arguments/results, provider event bodies, network candidates, call IDs, keys,
tokens, secret refs, or user identifiers beyond established protected dimensions.

The release gate includes source/schema scans for audio-bearing request fields and
capture/observer transports, secret canaries through fake Foundry responses, and
live network evidence that Broker has no long-lived media/observer connection.
It also injects canaries into server instructions, tool descriptions, tool
schemas, standing/ephemeral credentials, and upstream errors and proves none
appear in negotiation/status DTOs, Problem Details, logs, traces, metrics, or
storage. Malicious transcript and tool-result content that contains function-call
JSON, approval language, or Action IDs remains inert text/data and cannot create,
authorize, select, or execute an Action.

## Threats that must fail closed

- Cross-owner session/turn/tool access and identifier substitution.
- Client-selected provider URL/deployment/model/tool/account or redirect egress.
- Replay or payload change under negotiation, transcript, tool, or end keys.
- Oversized/malformed SDP, transcript, provider event, arguments, or result.
- Ephemeral/standing credential leakage through response, log, trace, metric,
  Problem Details, database, crash dump, or test artifact.
- Transcript or tool-result prompt injection that attempts to authorize an Action.
- Spoken approval, synthesized voice, or replayed transcript used as authorization.
- Permission confusion from Electron subframes or iOS background transitions.
- Session expiry, provider 429, network loss, app crash, conversation change,
  account revoke, plugin disable, or grant change during a call.
