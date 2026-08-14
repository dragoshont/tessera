# R2 Remote Hosts Test Contract

**Status:** Required phased gates for the Mac reference journey. Current
implementation evidence covers the v18 registry, v19 signed channel/lease,
the canonical Job subset, and the v20 server artifact slice. Client/helper/live
and physical-device gates remain pending and must not be represented as shipped.

Mocks prove contract behavior, not Secure Enclave protection, login-item lifecycle,
Cloudflare transport, notarization, physical Mac execution, APNs or iPhone approval.

## Backend and protocol

| ID | Required deterministic test |
|---|---|
| RH-B01 | v18-v20 migrations are additive/idempotent; older data remains readable and no forbidden private/path/secret columns exist |
| RH-B02 | Initiator-generated ticket has 256-bit entropy; create accepts only canonical SHA-256, atomically commits pairing+receipt, exact/concurrent replay returns identical metadata, changed replay conflicts, and DB stores no recoverable secret |
| RH-B03 | Claim validates strict canonical/on-curve P-256 JWK and low-S ES256; visual code derivation/mismatch/attempt bounds pass; confirmation is owner/version scoped and grants only requested intersection |
| RH-B04 | Expired/canceled/confirmed pairing and cross-owner pairing/Host reads fail without disclosure |
| RH-B05 | Immediate-transaction races prove one revocation/complete order: revoke-first requeues acknowledged work only with previously accepted signed NOT_STARTED proof, otherwise reconciles; complete-first preserves result then blocks future work |
| RH-B06 | ES256 vectors bind method/closed operation/concrete lease target/body/Host/protocol/key/message/next-sequence/time; duplicate/comma-joined headers, noncanonical IDs/decimal/hex, lease-ID substitution, malformed/off-curve/padded/high-S/downgrade/changed fields, gaps/overflow fail; exact message replay returns receipt, changed replay fails, rollback does not consume, deterministic rejection does |
| RH-B07 | Capability advertisements and capability/resource grants are separate, versioned, owner scoped; leases snapshot exact capability/resource grant versions, access mode and fingerprint and recheck them at ack/event/complete |
| RH-B08 | Execution policy is typed; client cannot send path, URL, executable, image, environment, shell, protocol endpoint or arbitrary capability |
| RH-B09 | Ack atomically binds immutable localAttemptId; existing scheduler fence creates one Host lease per run/Host; wrong Host/fence/hash/attempt/version/expiry cannot poll-resume/ack/event/complete/reconcile |
| RH-B10 | Offline/missing Host creates one durable blocker without changing canonical intent; zero Hosts does not affect server Jobs |
| RH-B11 | OFFERED work may expire; unexpired acknowledged work resumes only with same-attempt reconciliation; signed same-attempt NOT_STARTED proof permits QUEUED plus new lease/fresh fence; unknown start enters reconciliation and never duplicates execution |
| RH-B12 | Event sequence/batch/type/size bounds persist product checkpoints; output is not approval, Evidence or Memory |
| RH-B13 | Strict decode -> newline normalize -> control removal -> redact -> byte truncate -> SHA order is deterministic; no pre-redaction write occurs; declared/actual SHA and size match, replay is exact, and an accepted artifact blocks later `NOT_STARTED` requeue |
| RH-B14 | Cross-owner Host, resource, lease, event, artifact and Job access returns non-disclosing not-found |
| RH-B15 | Cross-language vectors prove exact sorted resource tuple encoding/hash; Action and one-use authorization persist exact Host ID, lease ID and that hash; substitution/version drift/replay deny, and Host/model/client text cannot approve |
| RH-B16 | Recovery after Broker restart fences stale polls/leases and recomputes blockers without claiming execution success |
| RH-B17 | API/log/Problem/DB DLP canaries contain no claim secret, private key, local path, raw command/env, signature, unbounded output or hidden prompt |

RH-B02/B03/B05/B07 additionally require concurrent exact/changed retries and an
injected exception immediately before transaction commit, proving counters,
state, grant history and receipts all roll back together. RH-B01 includes direct
SQL negative inserts for every closed v18 domain and a populated-v17 upgrade. A
zero-Host test runs an ordinary server Job through its existing lifecycle.

Run focused tests after each migration/service slice and `dotnet test Tessera.slnx`
at phase exit.

## Mac Host helper

| ID | Required local/native test |
|---|---|
| RH-M01 | P-256 abstraction signs canonical vectors; Keychain adapter stores no private key bytes in files/preferences/IPC |
| RH-M02 | Helper is separately enableable; Electron client remains usable when helper absent/disabled/revoked |
| RH-M03 | Pairing claim and signed poll operate over HTTPS with TLS/server descriptor validation, timeout and exponential backoff |
| RH-M04 | Descriptor-anchored resource mapping is opaque to server/renderer and rejects traversal, component/root/.git/HEAD/ref final symlinks, non-regular/oversized or `st_uid != geteuid()` metadata, pathname swap, wrong volume/device/inode/fingerprint, gitfile, commondir, alternates and missing root |
| RH-M05 | Fixed `host.repo.identity@1` launches no subprocess; HEAD/ref pread is capped at 257 with overflow rejection and matching pre/post fstat metadata; only canonical detached HEAD or refs/heads/object ID pass; packed refs/config/attributes/hooks/filters/commands/URLs/env are ignored/denied |
| RH-M06 | Local attempt journal survives process/network restart and reconciles before accepting a reoffer |
| RH-M07 | Lease expiry/revocation terminates process and prevents result acceptance; unknown outcome is reported honestly |
| RH-M08 | Nested helper packaging has minimal entitlements and renderer cannot invoke execution/private-key operations |

Physical/signed external gates: Secure Enclave/fallback protection, `SMAppService`
approval/login/reboot/update/unregister, sleep/wake/App Nap/network transitions,
Developer ID nested signing/notarization/Gatekeeper, real repository/Xcode profile,
and clean-machine install.

## Web/shared Electron UI

Storybook first:

- `Product/RemoteWorkspace`: Unsupported, Loading, ZeroHosts, PairingCodeEntry,
  PairingReview, PairingExpired, Populated, PartialError.
- `Product/RemoteHostDetail`: OnlineIdle, BusyRunning, OfflineWaitingForHost,
  UpdateRequired, Revoked, ApprovalRequired, Canceling, SucceededWithArtifacts,
  TruncatedArtifact, ExpiredArtifact.
- `Product/MacHostRolePanel`: ClientOnly, AvailableNotEnabled, Enrolled, Disabled,
  UpdateRequired.

Closed Host event types for the proof slice are `HOST_CONNECTED`,
`HOST_DISCONNECTED`, `JOB_ACCEPTED`, `STEP_STARTED`, `STEP_COMPLETED`,
`APPROVAL_REQUIRED`, `JOB_FAILED`, and `JOB_COMPLETED`. `ARTIFACT_AVAILABLE` remains
reserved for a future event contract; the v20 proof slice uses the signed artifact
upload route directly.
Unknown types fail; additional steering-taxonomy events require later protocol
versions.

Required tests:

- semantic table/list, keyboard/focus, 44 px targets, text/icon/timestamp status,
  polite status and bounded log announcements, reduced motion and axe;
- every visible control navigates/mutates or is disabled with reason;
- pairing expiry/retry/review, Host revoke, offline blocker, disconnect/resume,
  approval handoff, pause/cancel, bounded artifacts, responsive 320 px layout;
- Electron sender/origin validation, malformed route/deep link and notification
  navigate-only behavior, no execution/approval IPC.

## iOS

Required local/type tests and signed-device evidence:

- Remote tab/list/detail uses canonical API and client-only language;
- VoiceOver row value, Dynamic Type, 44 pt targets, Reduce Motion, text status;
- pairing review, Host offline/waiting, artifact preview, Action handoff and revoke
  confirmation;
- notification dedupe and deep link navigate only;
- signed-out/restored session and authorization failure do not expose cached Host
  data;
- physical iPhone receives approval/disconnect/completion notifications and can
  approve only through the existing exact Action screen.

## Live product gate

The run remains `BLOCKED_EXTERNAL` until retained evidence proves:

1. real homelab Broker/image/schema with Host API enabled;
2. packaged/notarized Mac helper paired with a physical Mac and one explicit
   Tessera repository resource;
3. real outbound Cloudflare path, disconnect/reconnect and server restart;
4. durable Job visible on Web, packaged Electron and physical iPhone;
5. fixed Host profile completes with bounded artifact/Evidence and no secret/path
   leakage;
6. offline Host yields `WAITING_FOR_HOST`, reconnect safely resumes, and unknown
   execution does not replay;
7. iPhone Action approval is exact and Host output cannot authorize;
8. zero-Host server functions and unrelated server Jobs continue.
