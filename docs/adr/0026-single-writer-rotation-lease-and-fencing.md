# ADR 0026 — Single-writer rotation via an acquired lease + fencing tokens

- Status: Proposed
- Date: 2026-06-24
- Relates to: ADR 0015 (Mode U rotation owner), ADR 0024 (session liveness is the
  rotator's truth), ADR 0025 (liveness is a first-class invariant). Grounds the SDD-03
  phase of the [liveness roadmap](../sdd/README.md); see the
  [cross-phase analysis](../sdd/cross-phase-adversarial-analysis.md) (attack A1).

## Context

The Mode U rotation owner rewrites a single-use session in place. **Two replicas rotating
the same session would corrupt it** (one consumes the token the other just wrote). Today the
only guard is an honor-system config assertion, `refresh.acknowledgeSingleWriter`: the
operator promises "exactly one replica." That promise is invisible to the runtime — a
horizontal scale-up, a rolling deploy that briefly runs two pods, or an accidental second
Deployment silently violates it, and the corruption is exactly the "RM went stale" class of
failure ADR 0024/0025 exist to prevent.

Martin Kleppmann's *How to do distributed locking* is decisive here: for **correctness** (not
mere efficiency) a lock must (1) come from a real consensus store, and (2) hand out a
**monotonic fencing token** that the protected resource checks, because a lease alone is
unsafe — a GC pause, a network delay, or a clock jump can let a holder act *after* its lease
expired while a new holder also acts. Redlock-style timing locks are explicitly unsafe for
this; ZooKeeper/etcd are the prescribed tools. The Kubernetes `Lease` API is etcd/Raft-backed.

## Decision

1. **Rotation runs only while the process holds a single-writer lease.** Introduce the seam
   `ISingleWriterLease.TryAcquireAsync()` → `IWriterLeaseHold?`. The rotation service acquires
   before every pass; `null` (another replica holds it) ⇒ the pass is **skipped** (inert),
   never run. This replaces the honor-system boolean with an *acquired* guarantee.
2. **Every hold carries a monotonic fencing token.** `IWriterLeaseHold.FencingToken` is
   strictly increasing across acquisitions. A rotation write performed under the lease carries
   the token; the store **must reject a write whose token went backwards** (defense-in-depth
   against a paused holder — the Kleppmann fencing rule).
3. **The real lease is a Kubernetes `Lease`** in `coordination.k8s.io` (etcd/Raft consensus),
   so exactly one replica holds it cluster-wide. Its `Lease` object + RBAC (`get/create/update`
   on leases, scoped to the namespace) are **plan-only infrastructure** — a human applies them
   (no apply by the agent; identity/RBAC is the highest blast radius).
4. **The default is `ProcessSingleWriterLease`** — always grants, issues an increasing token.
   It is correct **only** for a single-replica deployment (today's reality) and makes the seam
   real without requiring the cluster lease first. It is *not* multi-replica-safe; the
   Kubernetes-backed lease is.

## Consequences

- **Now (shipped, no infra):** the rotator gates on an acquired lease and a fencing token
  exists; behavior is unchanged for the single replica, but the single-writer rule is a
  runtime seam, not a promise. `acknowledgeSingleWriter` stays as the explicit opt-in.
- **Plan-only follow-on (human applies):** the `Lease` + RBAC manifest, and a
  `KubernetesSingleWriterLease : ISingleWriterLease` (a small Kubernetes-client adapter). Once
  applied, the broker can scale past one replica without risking session corruption.
- **Gated by SDD-05:** the read-through-on-401 refresh (SDD-05) is *also* a rotation write, so
  it must acquire the same lease and carry the same fencing token before it writes — this ADR
  is the prerequisite for SDD-05's write side (cross-phase analysis A1).
- **Store-side fencing enforcement** (rejecting a stale token) requires the credential store to
  expose a compare-and-set / version (Azure Key Vault ETags; OpenBao CAS). That enforcement is
  tracked with SDD-05; until then the lease (consensus) is the primary guard and the token is
  threaded but not yet rejected store-side.

## Alternatives rejected

- **Keep the honor system.** Invisible to the runtime; a scale-up silently corrupts. Rejected.
- **Redlock / Redis lease.** Timing-dependent and tokenless — Kleppmann shows it is unsafe for
  correctness. Rejected.
- **A database advisory lock.** Tessera has no shared RDBMS; the cluster already has etcd via
  the Kubernetes `Lease` API. Rejected as needless new infra.
