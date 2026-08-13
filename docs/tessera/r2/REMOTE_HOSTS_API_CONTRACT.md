# R2 Remote Hosts API And Protocol Contract

**Status:** Accepted local implementation contract

All user routes are under `/api/v1`, require the existing verified owner boundary,
and return `Cache-Control: no-store`. Unknown fields are rejected. Every mutation
requires `Idempotency-Key` and exact replay semantics.

## User/client routes

```text
POST /host-pairings
201 { pairingId, claimSecret, expiresAt, state, version }

GET /host-pairings/{pairingId}
200 { pairingId, state, requestedHost|null, expiresAt, version }

POST /host-pairings/{pairingId}/confirm
{ expectedVersion, confirmationCode, displayName,
  capabilityGrants[], resourceGrants[] }
201 HostDto

POST /host-pairings/{pairingId}/cancel
{ expectedVersion }
200 ResourceVersion

GET /hosts
200 Page<HostSummaryDto>

GET /hosts/{hostId}
200 HostDetailDto

PUT /hosts/{hostId}/grants
{ expectedVersion, capabilityGrants[], resourceGrants[] }
200 HostDetailDto

POST /hosts/{hostId}/revoke
{ expectedVersion }
202 HostDetailDto

GET /hosts/{hostId}/activity
200 Page<ActivityDto>

GET /job-runs/{runId}/remote
200 { blocker, lease|null, host|null, checkpoints, artifacts }

GET /job-runs/{runId}/remote-artifacts
200 Page<HostArtifactDto>
```

`claimSecret` is returned once. It is accepted only by the Host claim route and is
never returned by later reads, logs, events, or Problem Details.

## Host routes

Host channel routes use the signed request envelope below and do not accept user
bearer tokens as Host identity. The pairing claim route is the sole exception: it
is authenticated by the one-time claim secret because the Host identity does not
exist yet.

```text
POST /host-pairings/{pairingId}/claim
{ claimSecret, publicKeyJwk, protection, platform, architecture,
  agentVersion, protocolVersion, requestedCapabilities[], requestedResources[] }
202 { pairingId, state: CLAIMED, expiresAt, version }

POST /host-channel/poll
{ maxWaitSeconds: 1..25,
  activeAttempt: { leaseId, localAttemptId, state: NOT_STARTED|STARTED|COMPLETED }|null }
200 { serverTime, nextPollAfterMs, lease|null, command|null }

POST /host-channel/leases/{leaseId}/ack
{ leaseVersion, localAttemptId, accepted, rejectionCode|null }
200 ResourceVersion

POST /host-channel/leases/{leaseId}/events
{ leaseVersion, localAttemptId, events[] }
200 ResourceVersion

POST /host-channel/leases/{leaseId}/complete
{ leaseVersion, outcome, exitCode|null, output, outputSha256,
  truncated, verification, localAttemptId }
200 { lease, run, replayed }

POST /host-channel/leases/{leaseId}/reconcile
{ leaseVersion, localAttemptId, observedState, outputSha256|null }
200 { resolution, lease, run }

The helper generates one canonical local attempt ID before acknowledgement.
Accepted acknowledgement atomically stores it on the lease and it is immutable.
Every later event, completion, poll active-attempt report and reconciliation must
carry the exact same value. A mismatch is `host_attempt_mismatch` and cannot prove
non-execution.
```

## Signed Host request

Headers:

```text
X-Tessera-Host-Id
X-Tessera-Host-Protocol-Version
X-Tessera-Host-Key-Version
X-Tessera-Host-Operation
X-Tessera-Host-Target-Id
X-Tessera-Host-Message-Id
X-Tessera-Host-Sequence
X-Tessera-Host-Timestamp
X-Tessera-Host-Body-SHA256
X-Tessera-Host-Signature
```

The canonical UTF-8 signing input is exactly:

```text
TESSERA-HOST-V1\n
<HTTP_METHOD>\n
<OPERATION>\n
<TARGET_ID>\n
<hostId>\n
<protocolVersion>\n
<keyVersion>\n
<messageId>\n
<sequence>\n
<unixTimestampSeconds>\n
<lowercaseHexBodySha256>
```

Every listed header occurs exactly once after HTTP field-name case folding;
duplicates, comma-joined values, leading/trailing whitespace and non-ASCII fail.
Host, lease-target and message IDs match lowercase
`[a-z0-9][a-z0-9-]{0,63}`; poll target is the sole exception and is exactly `-`.
Protocol/key versions and sequence are ASCII `0|[1-9][0-9]{0,18}` parsed into
nonnegative Int64, with protocol/key fixed to `1` and sequence at least `1`.
Timestamp is ASCII `0|[1-9][0-9]{0,11}`, parsed as nonnegative Unix seconds no
greater than `253402300799`; signs, negative zero and leading zeroes fail. Body
hash is exactly 64 lowercase hexadecimal characters. Empty bodies hash the
zero-byte string. The receipt `requestHash` is lowercase SHA-256 of the complete
canonical signing-input bytes above.

`OPERATION` is a closed server-derived value: `poll`, `lease-ack`, `lease-events`,
`lease-complete`, or `lease-reconcile`. `TARGET_ID` is `-` for poll and the exact
canonical lowercase lease ID from the matched route for every lease operation.
Host endpoints reject every query string. The matched route must equal the signed
operation and target; lease-ID substitution invalidates the signature.

The signature is JOSE ES256 (`r || s`, exactly 64 bytes) encoded base64url without
padding. `r` and `s` are in `[1,n-1]` and `s` must be low-S (`s <= n/2`). The
public key JWK has exactly `kty="EC"`, `crv="P-256"`, optional `alg="ES256"`, no
private `d`, no unknown members, and canonical unpadded `x`/`y` values that each
decode to exactly 32 bytes. The point must be on P-256 and not infinity. Its
thumbprint is RFC 7638 SHA-256 over canonical `{crv,kty,x,y}` JSON.

Protocol version is exactly `1`; downgrade/upgrade attempts fail before sequence
consumption. Key version is exactly `1` in the proof slice. Rotation is deferred:
replacing a key requires revoking and re-pairing the Host.

Requests fail before business logic unless:

- Host is `ONLINE|BUSY|DEGRADED` and not revoked;
- key version/public key match;
- timestamp is within 300 seconds; configuration may reduce but not increase it;
- body hash and route binding match;
- sequence equals persisted sequence plus one and is in `1..Int64.MaxValue`;
- signature verifies.

Each signed endpoint parses canonical fields and verifies body/signature first,
then uses one immediate SQLite transaction. Inside the transaction it resolves
the signed Host/key (including revoked keys for receipt lookup only), then first
looks up `(owner,host,messageId)`: an equal operation/target/requestHash returns
the stored response without lifecycle or sequence evaluation; a mismatch is
`host_replay`. For a new message it requires active lifecycle, current key, the
next sequence and current grants, then performs the
business transition;
advances `lastAcceptedSequence`; and stores an exact response receipt bound to
operation/request hash/status/body. A rollback consumes nothing. A deterministic
business rejection commits its response receipt and consumes the envelope. Exact
duplicates replay that response; changed duplicates fail. Stable
failures are redacted: `host_auth_invalid`, `host_revoked`, `host_replay`,
`host_sequence_invalid`, `host_clock_skew`, `host_protocol_unsupported`.

Pairing user-route failures are closed: `pairing_not_found`, `pairing_expired`,
`pairing_canceled`, `pairing_consumed`, `pairing_attempts_exceeded`,
`pairing_confirmation_mismatch`, `pairing_grant_not_requested`,
`pairing_version_conflict`, and `pairing_invalid_request`. Cross-owner access uses
`pairing_not_found`.

## Pairing

`POST /host-pairings` creates 32 random bytes and stores only SHA-256. In the
reference Mac flow Electron main passes the secret to the helper through an
anonymous stdin pipe. It never enters argv, URL/deep link, clipboard,
notification, preferences, renderer persistence, logs, or crash metadata. Ticket TTL
is at most five minutes, one active ticket per owner by default, and five failed
claims consume/cancel the ticket. Claim atomically consumes the secret and records
the pending public identity and requested grants. The helper and server derive the
same six-digit visual code. Encode the canonical ASCII pairing ID, one `0x00`,
then the 32 raw RFC 7638 thumbprint bytes; SHA-256 that byte string, interpret the
first four digest bytes as an unsigned big-endian integer, reduce modulo 1,000,000,
and render exactly six ASCII decimal digits with leading zeroes. The native helper
displays it and the authenticated user enters it. Confirmation is
owner-authenticated, version-bound, limited to five failed
code attempts, constant-time compared, and grant-limited to the claim. The code is
never persisted. A canceled, expired, consumed, or confirmed ticket cannot be reused.

## Host DTO boundary

Public DTOs omit claim-secret hash, public-key bytes, accepted message IDs,
filesystem paths, local command, environment, and raw signatures. Resource DTOs
contain opaque ID, type, display name, fingerprint, access mode and lifecycle only.

## Lease command boundary

A command contains only:

```text
commandId, leaseId, leaseVersion, runId, schedulerFence, profileId,
capabilityId, capabilityVersion, capabilityGrantVersion,
resources[{resourceId,resourceGrantVersion,accessMode,fingerprint}],
inputHash, issuedAt, executeUntil,
outputLimitBytes, eventLimit
```

The Host resolves executable, argv, local path, environment and scratch directory
from locally installed reviewed profile/resource mappings. The server/client never
supplies them.

## Consequential Action binding

Host-backed consequential work uses the existing Action API and state machine.
Its public approval DTO includes friendly Host and resource summaries plus the
opaque `hostId`, `hostLeaseId`, and `hostResourceGrantHash` being authorized.
`ActionR2Binding` persists those exact values on both the proposed Action and the
one-use authorization. Authorization consumption compares Host, lease and resource
hash together with capability, payload, target, account/plugin and execution.
Host output, signed requests, model text, client flags and notifications cannot
create or consume authorization.

## Bounds

- request body: 64 KiB unless artifact endpoint specifies lower;
- pairing names/versions/IDs: 1..128 printable characters under closed patterns;
- capabilities/resources per Host: 64 each;
- poll wait: 25 seconds;
- event batch: 50 events / 64 KiB;
- text output: 32 KiB after UTF-8 normalization/redaction;
- inline artifact: 256 KiB; larger artifacts are unavailable in the proof slice;
- one active lease per Host in the proof slice.
