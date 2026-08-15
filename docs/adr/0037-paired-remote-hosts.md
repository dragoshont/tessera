# ADR 0037: Paired Remote Hosts use server-owned Jobs and signed Host requests

**Status:** Local reference implementation complete; live enrollment and deployment require external verification

## Context

Tessera already owns durable owner-scoped Conversations, Jobs, JobRuns, Actions,
Evidence, outputs, scheduler leases, and cross-client APIs. Some work must execute
on a particular trusted computer because it requires local repositories, Xcode,
local applications, or private local services. Treating that computer as another
server, scheduler, arbitrary shell, or remote desktop would duplicate canonical
state and widen authority beyond the Job.

The current public access path is ordinary HTTPS through Cloudflare Tunnel.
Remote Host transport must tolerate disconnects and edge restarts. macOS device
identity must use an OS-protected key rather than Electron preferences or a shared
password.

## Decision

A Remote Host is a replaceable, owner-scoped execution worker. Tessera Server
remains canonical for Host enrollment, capability/resource grants, Jobs, leases,
Actions, blockers, artifacts, Evidence, and audit.

The macOS reference Host is a separately enabled Swift login-item helper bundled
with the Electron application. Installing or opening the client does not enable
hosting. The helper owns one P-256 signing key in Keychain, preferring Secure
Enclave where available. The server stores only the public key and protection
grade. Electron renderer code never receives the private key, raw Host protocol,
or execution controls.

The Host initiates outbound authenticated HTTPS requests. The first transport is
bounded long-poll lease pull because it fits the existing Cloudflare route and
proves durable lease semantics without adding a connection registry. Host requests
use a transport-neutral signed envelope containing protocol/key versions, Host ID,
message ID, monotonic sequence, timestamp, a closed server-derived operation and
its concrete target ID, and body SHA-256.
TLS authenticates the server. Every accepted sequence and message ID is persisted.
A later WebSocket adapter may carry the same envelopes; it may not change Job,
lease, grant, or replay semantics.

Pairing is authenticated and explicit:

1. an authenticated owner creates a five-minute single-use claim ticket;
2. the helper claims it with its P-256 public key and requested capability/resource
   metadata;
3. an authenticated owner confirms the exact Host, capability classes, and opaque
   resources;
4. only then does the Host become dispatchable.

The 256-bit claim secret is returned once and only its hash is persisted. Pairing
attempts are bounded. Revocation invalidates new and active Host leases, rejects
later signed requests, and preserves historical Jobs and Evidence.

Capabilities and resources are independent grants. The server never stores or
accepts a Host filesystem path. The Host maps an opaque resource ID to one
user-selected canonical root locally and rejects path/symlink escape.

Remote execution reuses the existing scheduler fence. A Host work lease binds one
owner, Job, JobRun, Host, capability, resource set, exact input hash, scheduler
fence, attempt, issued time, and expiry. Missing/offline Host state is represented
by a blocker projection (`WAITING_FOR_HOST`, `WAITING_FOR_CAPABILITY`,
`WAITING_FOR_RESOURCE`, `HOST_DISCONNECTED`), not a second Job state engine.
Unknown side effects enter existing reconciliation semantics and are never replayed
blindly.

The first Host profile is one fixed, no-client-arguments, native repository
identity inspection with no subprocess. The later Git status and Xcode test profiles remain gated until the signed native
helper, resource picker, process confinement, and physical Mac journey pass. No
raw terminal or general shell is exposed.

## Consequences

- Tessera works normally with zero Hosts.
- Host replacement does not move canonical state.
- The first transport may be less immediate than WebSocket, but reconnect and
  replay behavior are deterministic and testable.
- A native helper adds signing, login-item, Keychain, update, and notarization
  obligations; those remain distinct from Electron client packaging.
- Additive migrations are retained on rollback. Rollback disables Host dispatch,
  expires leases, and preserves history.

## Rejected alternatives

- Electron `safeStorage` for device identity: encrypted storage is not a
  non-exportable device signing key.
- WebSocket first: adds live connection/backpressure complexity before durable
  lease and reconciliation semantics are proven.
- gRPC/mTLS through the current public Cloudflare hostname: incompatible with the
  deployed access path and disproportionate to the reference journey.
- Arbitrary shell, SSH/VNC, full filesystem, Docker socket, or Host-owned Jobs:
  these violate the trust boundary and product definition.
