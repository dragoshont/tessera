# R2 Realtime Voice Product Contract

**Status:** Accepted contract; local implementation authorized, deployment remains human-gated

## Outcome

Tessera Web, packaged macOS Electron, and native Expo iOS provide the same
foreground speech-to-speech experience inside the canonical Chat conversation.
The feature uses Microsoft Foundry `gpt-realtime-2.1` version `2026-07-07`
through the server-owned deployment `tessera-realtime-21`.

Raw microphone and model audio flow only over the negotiated WebRTC media path
between the client and Foundry. Tessera authenticates and authorizes, procures and
consumes the ephemeral secret, proxies only SDP signaling, persists completed
text transcripts, and relays approved tools. Tessera never receives, records,
buffers, transcodes, observes, logs, or persists raw audio.

## Canonical reuse

| Concern | Existing owner | Voice delta |
|---|---|---|
| Identity and ownership | Existing OIDC/native auth and server-derived owner | No voice identity or client-supplied owner |
| Conversation history | `Conversation`, `Message`, `MessagePart` | Persist completed client-reported transcript text |
| Public progress | Sequenced `ExecutionEvent` and Conversation SSE | Add closed realtime lifecycle/transcript/tool event types |
| Tool availability | `CapabilityAvailabilityService` | Evaluate at session start and before every tool dispatch |
| Tool execution | `ExecutionCoordinator`, `CapabilityCall`, `CapabilityResult` | Bind a realtime client call ID to canonical records |
| Consequential effects | `Action`, one-use authorization, verification | Spoken assent never approves; reuse `ActionApprovalCard` |
| Long-running work | `Job` and `JobRun` | Reuse only when the selected capability already creates a Job |
| UI | `Product/ChatWorkspace`, `ActionApprovalCard`, current tokens | Add a compact voice control/state region; no parallel chat page |

## Session journey

1. The authenticated user opens an ACTIVE conversation and explicitly activates
   the microphone control.
2. The client requests OS/browser microphone permission before contacting the
   realtime session route. Denial leaves Chat and typed input usable.
3. The client creates the local audio track, remote audio sink, data channel, and
   SDP offer, then sends the offer to Tessera.
4. Tessera checks owner, conversation, realtime readiness, concurrency/rate limit,
   and current tool availability; it performs the one-request Foundry negotiation.
5. The client applies the SDP answer. Only a successful peer/data-channel state
   is shown as Listening; an issued answer alone is labeled Negotiated, never Live.
6. User and assistant completed transcript events render as captions. Completed
   turn pairs are persisted idempotently as canonical Messages.
7. Tool calls cross the Tessera API as structured, bounded proposals. Safe calls
   can return a canonical bounded result. Consequential calls become Actions and
   pause that tool response until out-of-band approval and verification.
8. End, expiry, sign-out, conversation switch, app termination, permission loss,
   or platform interruption stops every local media track, closes the data channel
   and peer connection, and sends a best-effort metadata-only end receipt.

## Product states

The contract uses explicit text plus icon/state; color and animation are never the
only signal.

| State | User-visible behavior | Allowed action |
|---|---|---|
| `UNAVAILABLE` | Voice control disabled with a stable reason such as not configured or quota blocked | Continue typed Chat |
| `IDLE` | Microphone control labeled "Start voice" | Start |
| `REQUESTING_PERMISSION` | OS/browser prompt is pending; no Tessera session exists | Cancel when platform permits |
| `PERMISSION_DENIED` | Inline reason and platform Settings recovery | Continue typed Chat, open Settings where supported |
| `NEGOTIATING` | Bounded progress status; microphone is not presented as connected | End |
| `LISTENING` | Persistent microphone-active indicator and visible captions | Mute, End |
| `USER_SPEAKING` | Caption/status announces user speech without continuous screen-reader chatter | Mute, End |
| `ASSISTANT_SPEAKING` | Remote audio plus transcript caption; barge-in remains available | Interrupt, Mute, End |
| `TOOL_RUNNING` | Named capability and bounded progress | End; inspect canonical activity |
| `APPROVAL_REQUIRED` | Existing Action approval card/route; voice does not accept spoken approval | Approve or cancel out of band |
| `INTERRUPTED` | Network/audio-route/system interruption is stated; capture is stopped when platform requires | Explicit retry |
| `SESSION_EXPIRED` | Session ended and tracks are stopped; transcript already committed remains visible | Start a new session |
| `ERROR` | Stable, redacted failure and recovery action | Retry or continue typed Chat |
| `ENDING` | Controls are disabled while local resources close | None |

There is no automatic reconnect that silently reacquires the microphone. A retry
creates a new peer connection, SDP offer, and negotiation attempt.

## Platform behavior

### Web

- Requires a secure context, `RTCPeerConnection`, `mediaDevices.getUserMedia`,
  audio output, and a user gesture.
- A temporary hidden tab does not claim the session ended. `pagehide`, sign-out,
  conversation change, or route teardown closes it.
- Browser autoplay failure is an explicit `AUDIO_OUTPUT_BLOCKED` recovery state.

### Packaged macOS Electron

- Reuses the Web Chat component and renderer WebRTC; there is no main-process
  provider or media client.
- Permission handlers allow only `media` with `mediaTypes=["audio"]`, from the
  pinned `app://tessera` main frame and current main `webContents`. Camera,
  display capture, geolocation, notifications-through-permission, MIDI, USB,
  serial, Bluetooth, and subframe requests remain denied.
- The packaged app declares a specific microphone purpose string. Closing the
  window, signing out, or renderer crash ends capture.

### Native Expo iOS

- Requires a development/release build and an approved native WebRTC dependency;
  Expo Go is unsupported.
- `NSMicrophoneUsageDescription` is specific to realtime conversation. Camera
  permission and background audio mode are not requested.
- `AVAudioSession` behavior, Bluetooth/wired route changes, phone/Siri
  interruptions, Media Services reset, app inactive/background, lock, sign-out,
  and conversation/server changes are explicit lifecycle cases.
- The first release ends voice on background/inactive interruption and requires
  explicit restart. It does not operate as VoIP or receive incoming calls.

## Accessibility contract

- Captions are on and visible by default for both speakers.
- Start, mute/unmute, interrupt, and end controls have stable accessible names,
  state/value, visible focus, and keyboard activation on Web/Electron.
- Web touch targets are approximately 44 CSS px and never below WCAG 2.2's 24 px;
  iOS controls are at least 44 pt and support Dynamic Type and VoiceOver order.
- Status uses a polite live region for discrete transitions only. Transcript
  deltas are visually updated but not announced character-by-character.
- Motion is secondary to labels and respects reduced-motion settings.
- Permission denial, expiration, tool approval, mute, and errors are not
  communicated by color alone.

## Capability honesty

Voice is `READY` only while a server-owned Foundry readiness result is fresh. On
startup, after configuration change, and at most once per five-minute cache window,
`RealtimeReadinessService` calls the fixed GA client-secret endpoint with a
minimal non-media session configuration, a 10-second timeout, and no tools or
conversation content. Success requires a syntactically valid ephemeral value and
expiry at least 10 seconds in the future; the value is immediately discarded.
The probe never negotiates SDP and `GET /realtime-voice/status` never triggers it.

Until a probe completes, status is `CHECKING`. A missing configuration/deployment,
401/403, quota/rate limit, malformed response, timeout, or stale result maps to
`BLOCKED` with a stable redacted code. A successful real negotiation refreshes
readiness; any provider negotiation failure invalidates it immediately. Clients
render the returned state. A missing quota, deployment, native module, microphone
permission, secure context, or audio route is never rendered as Connecting or
Live.

## External gates

The following are outside local application implementation and remain human-owned:

1. Azure usage remains within the confirmed Sweden Central `gpt-realtime-2.1`
  quota (`0/10` allocated when verified).
2. A human creates `tessera-realtime-21` from model `gpt-realtime-2.1`, version
   `2026-07-07`, with reviewed capacity; the existing 1.5 deployment is retained.
3. Private GitOps references the deployment and approved managed identity or
   secret-store reference without committing a credential.
4. Network policy allows authenticated Broker HTTPS to Foundry signaling and
   client WebRTC/ICE to the documented Azure service endpoints (including
   required UDP/TCP 3478 where applicable), without routing media through Tessera.
5. Apple signing, microphone privacy review, a physical iOS device, and a signed
   macOS package are available for final device/TCC tests.

No Azure deployment, quota request, RBAC change, secret materialization, GitOps
apply, Flux reconcile, restart, or runtime mutation is authorized by this contract.
