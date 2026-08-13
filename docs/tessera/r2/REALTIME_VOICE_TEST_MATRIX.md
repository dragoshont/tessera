# R2 Realtime Voice Test Contract

**Status:** Local implementation gates pass; live external gates blocked

No feature phase is complete until all applicable automated gates pass and every
external/manual row is recorded with evidence. Mocks prove application behavior,
not Foundry media, Azure quota, OS permission prompts, signing, or device audio.

## Backend and shared contract tests

Run focused tests during implementation and `dotnet test Tessera.slnx` at phase
exit.

| ID | Required automated test |
|---|---|
| RV-B01 | Single-flight startup/config/expiry readiness probe uses fixed minimal no-tool/no-conversation session, 10-second timeout and five-minute freshness; status GET never probes; pending/stale/config/auth/quota/deployment/malformed/timeout states never claim Ready |
| RV-B02 | Authenticated owner can negotiate only an ACTIVE owned Conversation; unauthenticated, other-owner, archived/deleted, and substituted IDs fail closed |
| RV-B03 | Negotiation accepts one bounded SDP string and forwards raw `application/sdp` only to the fixed GA calls endpoint with redirects disabled |
| RV-B04 | Client-supplied endpoint, deployment, model, version, voice, instructions, tool schema, owner, and unknown fields are rejected |
| RV-B05 | Fake Foundry response is consumed server-side; token/key/resource/deployment/Location/upstream body plus instruction/tool-description/schema canaries are absent from DTO, Problem Details, logs, traces, DB, and metrics |
| RV-B06 | Missing token, malformed/oversized SDP answer, 401/403/429/5xx, timeout, cancellation, and redirect map to stable redacted failures |
| RV-B07 | Same in-flight negotiation attempt serializes; changed replay conflicts; expired in-memory replay requires a fresh attempt without a silent second mint |
| RV-B08 | Crash before mint, during mint, after secret, after Foundry SDP success, and before durable completion leaves one fenced generation; startup expires stale `NEGOTIATING` to outcome-unknown, never remints/resumes, and no state claims media Active |
| RV-B09 | Completed turn atomically creates canonical user/assistant Messages, receipt, events, and idempotency result |
| RV-B10 | Interrupted/failed assistant disposition maps to canonical STOPPED/FAILED; blank, oversized, control, duplicate provider item, changed replay, and cross-session turns fail |
| RV-B11 | Transcript is not Evidence, Memory, authorization, provider verification, HTML, or an executable tool request |
| RV-B12 | Sorted session tool bindings persist before mint and generate the exact Foundry definitions; name resolves only through those rows plus current availability; unknown/version/schema/account/grant changes fail |
| RV-B13 | Safe tool returns one bounded canonical result; oversized/malicious result is normalized/redacted and function-call/approval/Action-ID text remains inert before relay |
| RV-B14 | Consequential tool creates exact Action and `APPROVAL_REQUIRED`; spoken/text/client approval flags cannot authorize it |
| RV-B15 | Existing exact Action approval, substitution/replay/expiry, dispatch recheck, verification, reconciliation, and no-blind-retry tests cover realtime binding |
| RV-B16 | Session end is metadata-only and idempotent; terminal session rejects new turns/tools while existing canonical results remain readable |
| RV-B17 | Source/API/schema DLP scan finds no audio bytes/blob/base64/URL/recording fields, RTP/TURN/audio WebSocket/observer transport, or provider credential response field |
| RV-B18 | Concurrent owner/global session limits and provider 429 do not exceed configured mint/negotiation bounds |

### Proven local backend criteria (2026-08-13)

The Phase 3 backend implementation proves these complete rows with deterministic
fake-provider and SQLite tests:

| ID | Result | Evidence |
|---|---|---|
| RV-B03 | `PASS_LOCAL` | Fixed GA client-secret/calls paths, raw `application/sdp`, audio-only 64 KiB offer/answer guards, and disabled redirects are covered by `RealtimeFoundryTransportTests` and `RealtimeVoiceEndpointsTests`. |
| RV-B04 | `PASS_LOCAL` | Strict request binding rejects every unknown property; the endpoint test proves a client model override is rejected before provider use. |
| RV-B09 | `PASS_LOCAL` | `RealtimeVoicePersistenceTests` proves the Message pair, parts, turn receipt, public event, and idempotency receipt commit together and fully roll back on a duplicate provider item. |
| RV-B16 | `PASS_LOCAL` | Endpoint tests prove metadata-only exact end replay, changed replay conflict, terminal turn/tool rejection, retained canonical Messages, and the exact `realtime_ended` event. |

The full solution suite passes 803 tests. It covers the remaining local readiness,
ownership, fencing, redaction, tool snapshot/drift/idempotency, consequential
Action and approval-reconciliation paths. This is deterministic local evidence,
not proof of live Foundry behavior or deployed privacy.

Shared-client tests run with:

```bash
npm --prefix packages/tessera-client run typecheck
npm --prefix packages/tessera-client test
```

They cover exact DTO/error parsing, 201 negotiation, answer size/type guards,
idempotency headers, abort propagation, transcript/tool/end requests, unknown enum
rejection, and prove no token/provider endpoint type exists in the public client.

## Web component and browser tests

Run:

```bash
npm --prefix web run lint
npm --prefix web run test -- --pool=threads --maxWorkers=1
npm --prefix web run build-storybook
npm --prefix web run build
npm --prefix web run test:e2e
```

Required Vitest/Storybook cases:

| ID | Required automated test |
|---|---|
| RV-W01 | Existing `Product/ChatWorkspace` renders VoiceUnavailable, Idle, RequestingPermission, PermissionDenied, Negotiating, Listening, UserSpeaking, AssistantSpeaking, ToolRunning, ApprovalRequired, Interrupted, SessionExpired, Error, Mobile, Dark, and ReducedMotion stories |
| RV-W02 | Start requires a user action; denied/dismissed/revoked permission leaves typed Chat usable and exposes recovery text |
| RV-W03 | Client creates audio-only peer/data channel, sends offer only to Tessera, expects no token/provider URL, applies answer, and attaches remote stream to audio output |
| RV-W04 | Negotiated answer does not render Listening until peer/data channel success; ICE/data/audio/autoplay failures render honest states |
| RV-W05 | Mute toggles only local track enabled state; End/route change/sign-out/pagehide/error/expiry stops every track, closes channel/peer, clears audio stream, and is repeat-safe |
| RV-W06 | Completed captions persist once; deltas do not persist; malformed/duplicate/late events and post-end callbacks are ignored by generation fencing |
| RV-W07 | Tool request posts structured call to Tessera; safe result relays once; Action approval uses existing component and spoken text cannot approve |
| RV-W08 | Keyboard and screen-reader names/states/order pass axe; discrete status uses polite live region; transcript deltas do not flood announcements; no color-only status |
| RV-W09 | Controls remain stable at desktop and 320 px mobile widths, approximately 44 px touch targets, visible focus, and reduced motion |

Required Playwright cases use Chromium fake media flags and a fake audio device for
deterministic permission/media behavior, plus a mocked Tessera signaling API. They
exercise allow/deny permission, successful offer/answer and remote-track UI,
cleanup on navigation, transcript persistence/reload, Action approval handoff,
mobile layout, keyboard-only operation, axe, and no request from the page to a
Foundry host. A separate real-browser external gate proves Foundry connectivity.

Local result (2026-08-13): 119 Web unit tests and 48 Playwright tests pass;
production and Storybook builds pass. The focused realtime hook suite proves
permission ordering, SDP application, cleanup, transcript persistence, canonical
tool output, out-of-band Action pause/resume, and interruption cleanup ordering.

## Electron tests

Run:

```bash
npm --prefix desktop test
npm --prefix desktop run test:electron
npm --prefix desktop run package:mac
npm --prefix desktop run verify:package
npm --prefix desktop run test:packaged:inspect
```

| ID | Required automated or package test |
|---|---|
| RV-E01 | Permission check/request allows microphone media only for current pinned main frame and denies camera, display capture, subframes, foreign origins, and all unrelated permissions |
| RV-E02 | Existing sandbox/contextIsolation/webSecurity/no-node/no-webview/pinned-navigation and narrow preload bridge remain unchanged; no media/provider IPC is added |
| RV-E03 | Electron renderer passes the shared Web voice tests with fake audio and cleans tracks on window close/renderer crash/sign-out |
| RV-E04 | Packaged `Info.plist` contains reviewed `NSMicrophoneUsageDescription`; package/fuse inspection passes and no broad entitlement/background capture is introduced |
| RV-E05 | Installed signed/notarized package prompts once through macOS TCC, handles deny and later Settings grant, captures/plays audio, and releases the system microphone indicator on End/quit |

`RV-E05` is a manual external checkpoint because unsigned automation cannot prove
real TCC, signing/notarization, microphone hardware, or speaker routing.

Local result (2026-08-13): TypeScript lint, 8 unit tests, one real Electron shell
test, DMG/ZIP packaging, package/fuse verification and packaged smoke pass.

## Expo iOS dependency and native tests

The implementation phase begins with a throwaway dependency spike, not production
UI. It must prove one maintained WebRTC module supports Expo SDK 57, React Native
0.86, New Architecture, CocoaPods, arm64 simulator and physical arm64 device,
audio-only peer/data channel, cleanup, and release archiving. Expo Go is not a test
target. Failure returns to planning; it does not justify a WebView or server audio.

The spike runs only in `$TMPDIR/tessera-realtime-voice-spike`, populated from a
filesystem copy of the current working tree so it includes the completed uncommitted
iOS OIDC work while excluding `.git`, `node_modules`, Pods, build, dist, and release
outputs. Before copying, record `git status --short` and SHA-256 hashes for every
tracked/untracked `ios/**` and `packages/tessera-client/**` source/config file.
Dependency install, config-plugin changes, CocoaPods, and `expo prebuild --clean`
run only in the temporary copy. Recompute status/hashes afterward; any original
worktree change is a failed spike. Production adoption is a later reviewed
`apply_patch` change, never copied wholesale from generated output.

Baseline and generated-native commands:

```bash
npm --prefix ios run typecheck
npm --prefix ios run prebuild -- --platform ios
xcodebuild -workspace ios/ios/Tessera.xcworkspace -scheme Tessera -configuration Debug -sdk iphonesimulator -destination 'platform=iOS Simulator,name=iPhone 17 Pro' test
xcodebuild -workspace ios/ios/Tessera.xcworkspace -scheme Tessera -configuration Release -sdk iphonesimulator -destination 'generic/platform=iOS Simulator' CODE_SIGNING_ALLOWED=NO build
```

The implementation must add an XCTest-capable test target or an equivalent
checked-in deterministic native test command before claiming iOS automation; the
current `ios/package.json` has typecheck only.

| ID | Required native/unit/device test |
|---|---|
| RV-I00 | Temporary-copy spike leaves the original dirty iOS/shared-client status and file hashes byte-identical before/after |
| RV-I01 | Config plugin/prebuild adds only `NSMicrophoneUsageDescription`; no camera/background-audio/VoIP capability; clean prebuild is reproducible |
| RV-I02 | Permission not-determined/granted/denied/restricted/revoked maps to exact states and typed Chat remains usable |
| RV-I03 | Native peer is audio-only, sends SDP only to Tessera, accepts no provider token/URL, opens data channel, plays remote audio, and exposes captions |
| RV-I04 | End is idempotent and releases tracks, peer, channel, delegates/listeners, timers, and `AVAudioSession` on every success/failure path |
| RV-I05 | App inactive/background, phone/Siri interruption, Media Services reset, route/Bluetooth/headset change, sign-out, server/conversation switch, expiry, and network loss end or recover exactly as contracted without silent microphone reacquisition |
| RV-I06 | Late async permission/SDP/data events cannot update a replaced conversation/session; generation fencing tests cover races |
| RV-I07 | VoiceOver labels/value/order, 44 pt targets, Dynamic Type, captions, no color-only state, and reduced motion pass Accessibility Inspector/manual device review |
| RV-I08 | Physical device on Wi-Fi and cellular completes bidirectional audio, barge-in, transcript persistence/reload, safe tool, out-of-band Action approval, Bluetooth route, interruption, and microphone release |

`RV-I07` manual Accessibility Inspector and `RV-I08` are external device gates.

Local result (2026-08-13): native typecheck and Release simulator build pass with
`react-native-webrtc`/`WebRTC.framework` embedded. The built bundle contains the
specific microphone purpose string, no camera purpose, and no background modes;
the simulator app reaches JavaScript startup. Native deterministic lifecycle unit
coverage and physical-device audio/permission/route evidence remain external gaps.

## Live Foundry and privacy gates

These remain `BLOCKED_EXTERNAL` until quota and deployment exist:

1. Azure evidence shows Sweden Central resource, exact deployment
   `tessera-realtime-21`, model/version `gpt-realtime-2.1`/`2026-07-07`, reviewed
   capacity, and nonzero quota.
2. Authenticated Web, packaged Electron, and physical iOS each negotiate through
   Tessera and exchange real bidirectional audio directly with Foundry.
3. Each client proves captions persist in the same canonical Conversation and a
   typed follow-up continues that history.
4. One safe tool and one consequential tool prove canonical result and out-of-band
   Action approval/verification behavior.
5. Network/process evidence shows Broker makes only short HTTPS client-secret and
   SDP calls, opens no observer/media connection, receives no audio content type,
   and has no audio/SDP/token in logs, traces, DB, backups, or test artifacts.
6. Provider 429, session expiry, network interruption, client crash, Broker restart
   during negotiation, and rollback to typed Chat fail visibly and release media.

## Rollout and rollback acceptance

- Canary is owner-scoped and server-controlled; clients never select 1.5 versus
  2.1 or a deployment name.
- Keep the existing 1.5 deployment at capacity 5 during the observation window.
- Stop new voice negotiations before rollback. Existing client sessions end
  locally; completed canonical transcript Messages remain.
- Roll back server-owned deployment selection through reviewed private GitOps;
   never down-migrate `v16` and never delete the 2.1 deployment until the owner
  accepts the observation window.
- A rollback is complete only when all clients honestly show Voice unavailable or
  the selected ready deployment, typed Chat remains available, and no microphone
  continues capturing.
