# ADR 0036: Client-Direct Foundry Realtime Voice

**Status:** Accepted; local implementation authorized, Azure/GitOps apply remains human-gated

## Context

Tessera must provide real, bidirectional voice on Web, packaged macOS Electron,
and native Expo iOS while migrating the existing `chat.hont.ro` experience from
Azure `gpt-realtime-1.5` to Microsoft Foundry `gpt-realtime-2.1`, version
`2026-07-07`. The server-owned deployment name is `tessera-realtime-21`.

The existing LibreChat fork uses the GA WebRTC protocol: its authenticated
server procures a short-lived client secret, returns that secret to the browser,
and the browser posts an SDP offer directly to Foundry. Microsoft documents a
more secure variant in which the application service performs only SDP
negotiation so the browser never receives the ephemeral secret.

The privacy boundary is stricter than the existing implementation: raw input or
output audio must never traverse or terminate at Tessera. Tessera may authenticate
and authorize a user, build a bounded session configuration, procure and consume
an ephemeral secret, relay an SDP offer and answer, persist client-reported text
transcripts, and relay canonical tool calls. The clients and Foundry are the only
media endpoints.

The target Azure resource `homelab-aoai-se-p5jwiq` is in Sweden Central. Its
existing `gpt-realtime-1.5` deployment has capacity 5. The subscription currently
reports `gpt-realtime-2.1` usage `0/10`, leaving ten available capacity units;
creating the reviewed 2.1 deployment remains a human-gated external change.

## Decision

Use Foundry's GA WebRTC endpoints and a Broker-held, one-request credential:

1. An authenticated client creates an audio-only `RTCPeerConnection`, adds its
   local microphone track and the `realtime-channel` data channel, and creates an
   SDP offer.
2. The client sends only that bounded SDP offer to the owner-scoped Tessera
   conversation route.
3. Tessera validates authorization and capability availability, constructs the
   server-owned session configuration, procures an ephemeral secret from
   `/openai/v1/realtime/client_secrets`, and immediately uses it to post the offer
   to `/openai/v1/realtime/calls`.
4. Tessera returns only the SDP answer and non-secret session metadata. It never
   returns the ephemeral secret, resource endpoint, deployment name, standing
   provider credential, or upstream response body.
5. The negotiated SRTP/DTLS media and WebRTC data channel flow directly between
   the client and Foundry. Tessera is not an ICE/TURN peer and does not open an
   observer/controller WebSocket.
6. Completed transcript turns are submitted as untrusted text and atomically
   materialized as canonical `Message` records. Tool requests are resolved through
   existing capability availability, `ExecutionCoordinator`, `CapabilityCall`,
   `Action`, authorization, verification, and Job contracts.

The realtime provider resource, endpoint, model ID/version, deployment name,
voice, instructions, VAD mode, transcription configuration, and tool definitions
are server-owned. Clients cannot override them.

## Product Boundary

- Voice is another interaction mode for the current canonical Conversation, not
  a second conversation store or a voice-only agent.
- Completed captions are visible during the session and persist as ordinary
  Conversation Messages. Transcript provenance remains client-reported and is
  not automatically promoted to Evidence, Memory, or authorization.
- A model function call is only a proposal. Spoken assent is never an Action
  approval. Consequential tools use the existing out-of-band Action approval UI.
- The first release is foreground, user-initiated voice. It has no recording,
  audio upload, telephony, observer, background-call entitlement, wake word,
  camera, or silent automatic session renewal.
- A session may last at most the provider limit. Expiry ends microphone capture;
  starting another session requires a fresh explicit user action.

## Client Decisions

- Web uses browser `RTCPeerConnection`, `getUserMedia`, and an autoplay audio
  element after explicit user activation.
- Electron reuses the Web component and browser WebRTC, but its main-process
  permission policy allows microphone access only for the pinned Tessera main
  frame and continues to deny camera and every unrelated permission.
- Expo iOS uses a reviewed native WebRTC module in a development/release build;
  Expo Go is unsupported. Dependency compatibility with Expo SDK 57, React Native
  0.86, New Architecture, CocoaPods, arm64 simulator/device, and App Store privacy
  metadata is an implementation-entry spike, not an assumption.
- iOS uses the native audio session and ends voice when the app backgrounds,
  becomes inactive for a system interruption, signs out, or changes server or
  conversation. No background audio mode is requested in this slice.

## Consequences

### Positive

- Raw audio cannot enter Broker logs, storage, traces, backups, or tool paths.
- No client handles a Foundry credential or provider endpoint.
- Existing Chat, Action, Job, auth, custody, and UI contracts remain authoritative.
- The old deployment can remain available as rollback without dual-writing data.

### Costs and risks

- Broker briefly handles SDP, which can include ICE/network metadata and must be
  bodyless in logs and absent from persistence.
- Transcript integrity is limited to authenticated client reporting because no
  Tessera observer is allowed. The UI and data contract label that provenance.
- Electron's current deny-all permission policy needs a narrowly scoped exception.
- Native iOS WebRTC is a material dependency and release-build risk.
- Live completion is blocked until Azure grants quota and a human applies the
  private deployment/GitOps change.

## Rejected alternatives

- **Return the ephemeral secret to clients:** proven by LibreChat but weaker than
  the mandate and Microsoft's documented SDP-proxy option.
- **Relay audio over Broker WebSockets:** violates the raw-audio prohibition and
  adds latency, compliance scope, and a second media transport.
- **Use Foundry's observer/controller WebSocket:** gives Tessera session content
  it does not need and weakens the privacy claim.
- **Create voice-specific tools or messages:** duplicates accepted R2 aggregates
  and creates cross-mode drift.
- **Wait for quota before writing contracts:** hides client and dependency risks
  without reducing the external deployment dependency.

## Governing contracts

- `docs/tessera/r2/REALTIME_VOICE.md`
- `docs/tessera/r2/REALTIME_VOICE_API_CONTRACT.md`
- `docs/tessera/r2/REALTIME_VOICE_DATA_MODEL.md`
- `docs/tessera/r2/REALTIME_VOICE_SECURITY_MODEL.md`
- `docs/tessera/r2/REALTIME_VOICE_TEST_MATRIX.md`

## Sources

- Microsoft Learn, *Use the GPT Realtime API via WebRTC*:
  https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/realtime-audio-webrtc
- Microsoft Learn, *Use the GPT Realtime API for speech and audio*:
  https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/realtime-audio
- Microsoft Learn, *Foundry Models sold by Azure*, Audio models:
  https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/models-sold-directly-by-azure#audio-models
- Read-only local LibreChat evidence: `api/server/routes/realtime.js` and
  `client/src/hooks/Realtime/useRealtimeVoice.ts` in the sibling checkout.
