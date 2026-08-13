# R2 Remote Hosts Product Contract

**Status:** Accepted reference-slice contract; implementation and live proof in progress

## Product invariant

Remote lets a user supervise durable Tessera work running on an explicitly paired
computer. The Job belongs to Tessera. The Host is a replaceable worker. Remote is
not remote desktop, a terminal dashboard, another scheduler, another Memory store,
or an implicit grant over the computer.

Web, packaged macOS, and iOS consume the same owner-scoped server API. macOS may
also run the optional Host helper, but client and Host roles are separately enabled
and revocable.

## Reference journey

The initial locally provable journey is deliberately narrower than the north-star
Xcode journey:

1. The authenticated owner enables Host mode on a Mac.
2. The native helper generates a P-256 device key and claims a short-lived ticket.
3. The owner confirms `host.repo.identity@1` and one opaque repository resource.
4. Tessera creates a normal one-shot Job and JobRun with Host execution policy.
5. If the Host is offline, the run remains durable with `WAITING_FOR_HOST`.
6. An online Host pulls one fenced lease and natively reads the descriptor-bound
  repository identity (branch/ref, commit and resource fingerprint), with no
  subprocess, shell or client arguments.
7. Bounded redacted output and integrity metadata return to canonical Job output,
   checkpoints, Activity, and the related Conversation.
8. Disconnect before execution resumes the same unexpired lease only after
  same-attempt reconciliation. Proven-not-started requeue uses a new lease and
  fresh scheduler fence. Unknown execution outcome is never blindly retried.

The Xcode simulator-test profile, push approval journey, signed background helper,
and physical iPhone supervision are subsequent reality gates, not shipping claims
for the proof slice.

## User-visible states

Host lifecycle:

```text
PAIRING | ONLINE | BUSY | DEGRADED | OFFLINE | REVOKED | UPDATE_REQUIRED
```

Run blocker projection:

```text
WAITING_FOR_HOST | WAITING_FOR_CAPABILITY | WAITING_FOR_RESOURCE |
HOST_DISCONNECTED | HOST_UPDATE_REQUIRED
```

A blocker explains why canonical work cannot currently proceed. It does not create
a new JobRun state machine. The existing JobRun remains `QUEUED`, `RUNNING`,
`WAITING_FOR_APPROVAL`, terminal, or reconciliation-required as appropriate.

## Host inventory

Remote home lists friendly name, platform/architecture, lifecycle, connection
status and source timestamp, agent/protocol versions, last seen, current Job,
capability summary, resource summary, pending approval count, and update state.
Network address is never identity.

Host detail order is:

1. identity and truthful status;
2. current Job and blocker;
3. exact approval if present;
4. durable progress checkpoints;
5. bounded artifacts;
6. granted capabilities and resources;
7. Activity history.

## Pairing and consent

Pairing does not grant execution. Confirmation displays the Host identity,
protection grade, requested capabilities, and requested opaque resources.
Capabilities and resources are approved independently and are versioned. General
shell and full filesystem do not exist in the reference slice.

Revocation prevents new work, invalidates active Host leases according to policy,
and rejects subsequent Host authentication. Historical Job, output, Evidence, and
Activity records remain visible.

## Controls

Every visible control works or is disabled with a reason:

- **Pair a Mac** creates or continues a real pairing flow.
- **View work** opens the canonical JobRun.
- **Pause after current step** changes durable Job intent and reports requested
  state until checkpointed.
- **Cancel Job** requires impact confirmation and never hides unknown side effect.
- **Review Action** opens the existing Action approval surface; notification and
  Host output cannot approve.
- **Revoke Host** requires confirmation and preserves history.

Freeform follow-up instruction is absent until a run contract can accept and
persist it safely.

Interactive Remote Sessions, follow-up instruction, automatic Host handoff,
WebSocket transport, key rotation, and Xcode execution are explicit future
slices. The proof slice supervises one durable JobRun; it does not introduce an
interactive session aggregate.

## Artifacts and progress

Progress is a sequence of bounded product checkpoints, not chain-of-thought or an
unbounded terminal stream. Percent is shown only when a truthful total exists.

Artifact rows expose kind, summary, media type, byte size, SHA-256, created/expiry
time, retention, redaction/truncation, and verification level. Preview supports
bounded text and reviewed images. Artifact content is untrusted input and never
becomes Memory, Evidence, approval, or authorization without a separate canonical
transition.

## Accessibility

Web uses semantic tables/lists, keyboard navigation, visible focus, text+icon
status, polite status regions, persistent alerts, and no streamed announcement
flood. iOS exposes VoiceOver values such as “MacBook Pro, offline, last seen 12
minutes ago, one Job waiting,” supports Dynamic Type and 44 pt targets, and keeps
approval/revocation confirmations user-dismissed. Reduced Motion removes decorative
progress animation without hiding state.

## Capability honesty

Until live evidence exists, product language is **Remote Host preview**. Web/iOS
may show `Unsupported` when the server lacks Host APIs. The Electron client shows
`Client only` unless the bundled helper exists, and `Available, not enabled` until
explicit Host consent. No UI claims background hosting, Secure Enclave, signed
notarized distribution, Xcode execution, Cloudflare recovery, or iPhone approval
until those gates pass.
