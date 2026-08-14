# R2 Remote Hosts Security Model

**Status:** Accepted; R4 implementation gate

## Trust boundary

The Host is trusted only for the explicitly granted capability/resource/lease.
Host identity is not a ConnectedAccount. Pairing is not execution authorization.
Advertisement is not a grant. Output is not Evidence, Memory, approval, provider
verification, or executable instruction.

Tessera Server remains authoritative for owner identity, grants, Jobs, scheduler
fences, Actions, blockers, accepted events, artifacts and revocation. The Host
keeps its device private key, local resource paths, local credentials, local
attempt journal and process state. It never receives the whole Memory database or
unrelated account credentials.

## Device identity

The macOS helper generates a P-256 signing key using Security.framework. Prefer a
Secure Enclave non-exportable key; fall back to a `ThisDeviceOnly` Keychain P-256
key and report the protection grade honestly. Private keys never enter Electron
renderer, server, logs, preferences, backups, crash reports or IPC.

The server accepts only the strict canonical P-256 JWK and low-S signature form
defined by the API contract and computes an RFC 7638 thumbprint. Key rotation is
deferred in the proof slice; a Host must be revoked and paired again. Revoked key
versions never authenticate again.

## Pairing threats

Controls:

- authenticated owner creates the ticket;
- 256-bit random claim secret, hash-only persistence, five-minute maximum TTL;
- single consumption and bounded failed claims;
- claim binds the public key, protocol, platform, agent version and requested
  grants before user confirmation;
- confirmation uses optimistic version and displays a short hash-derived code;
- expired/canceled/consumed/confirmed tickets cannot be replayed;
- response/log/Problem Details DLP canaries exclude secret/hash/signature content.

## Signed request threats

Every Host request signs method, closed route operation, Host/protocol/key version, message ID,
monotonic sequence, timestamp and exact body hash. The server verifies the
signature and atomically consumes message ID/sequence with the operation. Bounds
and closed parsers run before persistence. Clock skew and protocol/key versions
are bounded. Compression is disabled on future WebSocket transport.

A valid signature does not bypass lifecycle, owner, capability/resource grant,
Job state, scheduler fence, lease expiry, input hash or Action checks.

## Capability and resource threats

The wire contains stable capability IDs and opaque resource IDs only. The first
profile accepts no client arguments. At grant time the helper opens the root with
`O_DIRECTORY|O_NOFOLLOW`, walks every component using descriptor-relative
`openat`/`fstatat(AT_SYMLINK_NOFOLLOW)`, records volume UUID, device/inode and
repository fingerprint, and stores the display path only in helper-owned Keychain
data. System ancestors may be owned only by root or the helper process effective
UID. The selected repository root and every traversed directory/file beneath it
must have `st_uid` equal to the helper process effective UID; any other owner is
denied. `.git` must be a real directory below the root, not a gitfile or symlink;
`.git/commondir` and `.git/objects/info/alternates` must not exist. Immediately
before execution it repeats these descriptor-relative checks and retains the
verified root and `.git` directory FDs.

The proof profile is `host.repo.identity@1` and launches no child process. Native
Swift code opens `.git/HEAD` with descriptor-relative `openat(O_RDONLY|O_NOFOLLOW)`,
requires `fstat` regular-file type, `st_uid == geteuid()`, and a maximum 256-byte size, then reads
only from that retained FD using one positional read capped at 257 bytes. Exactly
257 bytes is overflow and fails. A second `fstat` must match device, inode, owner,
mode, size and modification/change timestamps from before the read; otherwise the
result is discarded. Ref files use the same 257-byte read and pre/post metadata
checks. It accepts either a
40/64 lowercase hex detached commit or `ref: refs/heads/<closed-segments>`; a ref
is resolved descriptor-relative beneath `.git/refs/heads` using no-follow opens
for every directory and final file, requires a regular file with
`st_uid == geteuid()` no
larger than 256 bytes, and must contain one canonical 40/64 lowercase hex object ID. Packed
refs, gitfiles, commondir, alternates, config, attributes, hooks, filters, external
commands, URLs and environment are not interpreted in the proof slice. The result
is a bounded `{branch|null, commit, resourceFingerprint}` object. Both FD
identities are checked again before accepting output; mismatch discards output and
reports reconciliation-required.

The Host does not ingest Keychain items, SSH keys, browser cookies, `.env`, cloud
credentials or arbitrary local MCP servers. Local MCP projection is future work
and must pass the same grant intersection.

## Lease, disconnect and side-effect threats

A Host lease is scoped and short-lived. It binds one owner/Host/JobRun/profile/
capability/resource grant versions/fingerprints/fence/input hash. Reuse for another
run or after expiry is denied. Revocation and signed Host operations use immediate
SQLite transactions, providing one total order: completion-first remains
historical and revocation blocks future work; revocation-first terminalizes
active leases as `REVOKED`, and later ack/event/complete is denied. The helper
must stop work when it observes revocation, but no post-revocation result is
accepted. A reconnect reports
local attempt identity/state before any reoffer.

Read-only work may be reoffered only when server and Host both prove execution did
not begin through an accepted signed `NOT_STARTED` reconciliation bound to the
same lease and local attempt. Missing `STEP_STARTED` is not proof. Any unknown
local/external side effect becomes `RECONCILIATION_REQUIRED`.
Host text, model text, client flags and notification actions cannot approve.
Consequential work uses the existing exact Action binding extended with Host and
resource identity: `hostId`, `hostLeaseId`, and the canonical sorted
resource-grant tuple hash are immutable on Action and authorization. Any
substitution or later grant-version drift denies consumption/dispatch.

## Artifact threats

Host events and artifact content are untrusted and may contain prompt injection,
terminal escapes, secrets or malicious markup. Strictly decode UTF-8, normalize
newlines, remove controls, redact configured secret patterns, truncate by bytes,
then hash the exact persisted bytes. Never write pre-redaction content. Enforce
declared and recomputed length/hash,
escape rendering, use plain-text preview, and limit batches/counts. Never execute,
render raw HTML, interpolate into shell, or automatically promote artifact content
to Context/Memory/Evidence.

## Renderer and client threats

Electron renderer receives only narrow Host status and enable/disable intents.
It cannot access the private key, signed envelopes, resource path, process handle,
raw local logs or Host auth token. Sender/origin validation remains mandatory.
Deep links and notifications navigate to validated IDs only; they never pair,
approve, execute, cancel or revoke directly.

iOS is client-only. No background Host mode, local execution or secret custody is
introduced.

## Required adversarial tests

- Host impersonation, malformed/JWK curve and signature variants;
- pairing secret brute/replay/expiry/cancel and cross-owner claim/confirm;
- revoked Host and old key-version reconnect;
- duplicate/out-of-order sequence and message ID;
- body, method and path substitution after signing;
- capability advertisement without grant and resource grant without capability;
- cross-owner Host/Job/lease/artifact access;
- expired/stale/wrong-fence/wrong-input lease completion;
- disconnect before start vs unknown in-progress outcome;
- resource traversal/symlink/fingerprint mismatch and arbitrary argv/environment;
- prompt-injection/secret/control/oversized artifact;
- renderer IPC/deep-link/notification attempts to execute or approve;
- zero Hosts and all Hosts offline leave unrelated server work healthy.
