# ADR 0025 — Liveness is a first-class invariant: brokered sessions are observably alive, self-healing, or fail loud

- **Status:** Proposed (2026-06-24)
- **Deciders:** maintainer (Dragoș)
- **Relates to:** the five security invariants in
  [architecture.md §6](../architecture.md#6-security-model-summary-full-threat-model-below)
  (this adds a **sixth**), [SECURITY.md](../../SECURITY.md) R1/R2/R3 (those are
  confidentiality/integrity; this adds the **availability** dimension),
  [ADR 0024](0024-session-liveness-and-oracle-driven-reseed.md) (the *mechanism* —
  oracle-driven re-seed), [ADR 0016](0016-admin-portal.md) /
  [ADR 0017](0017-awareness-dashboard.md) (the surfaces that must tell the truth),
  homelab **SDD-42** (the sessionkeeper-side loop).
- **Implemented (Tessera side) by:** the connection-health SDD (see
  [docs/research/liveness-ux-oss-gap-analysis.md](../research/liveness-ux-oss-gap-analysis.md)).

## Context

Tessera's threat model is excellent on **confidentiality and integrity**: every
invariant — verified identity, fail-closed, inject-never-handover, no token
passthrough, least-privilege+audit, the R1/R2/R3 residual risks — defends *the
secret* and *the deputy*. **None defends availability.**

Two production incidents were the inverse failure — the credential was perfectly
secret and perfectly **dead**:

1. **The 5-day silent death.** A Regina Maria session's refresh chain died; the
   keeper's `probe()` asserted only that the success *cookie was present* (an
   optimistic `ttl_hint` timer), so it read "healthy" for days. The awareness
   dashboard — the very surface meant to give the operator truth — showed **green
   while dead**. The user discovered the outage by trying to use it.
2. **The reverted Tessera cutover** (`ca148ee`, "RM session went stale on the
   target"). When Tessera became the rotator, the same un-observed staleness
   killed the session and the cutover was rolled back.

A transparency surface that reports a false "healthy" is worse than none — it
manufactures confidence. ADR 0024 fixed the *mechanism* (the harvester now
re-seeds on the rotator's real verdict). This ADR makes the *principle* explicit
so the gap cannot silently reopen in another component, surface, or cutover.

## Decision

**Add a sixth load-bearing invariant: _liveness is observable and self-healing, or
it fails loud._** Concretely, for every brokered connection:

1. **Truthful, use-based health.** Health is derived from *actually exercising the
   credential* (the rotator's real verdict), never from cookie-presence or an
   unchecked timer. The states are `healthy | stale | dead | needs-human`, each
   with **provenance**: `last_checked`, `last_rotated`, `expires_at` (when known),
   `consecutive_failures`.
2. **No silent degradation.** A `dead`/`needs-human` connection MUST surface
   **proactively** — an alert *and* a truthful state on the awareness dashboard —
   within minutes, never "when the user next tries it." A surface MUST NOT render
   `healthy` from an optimistic signal.
3. **Self-heal where safe, escalate loud where not.** Recovery is automatic and
   **breaker-gated** where a machine can do it (re-seed via the harvester);
   where only a human can (reCAPTCHA / MFA), it flips to `needs-human` and pages —
   it never silently waits, and never storms the provider (the login circuit
   breaker is the safety).
4. **Health is a first-class broker output.** The verdict is exposed by the broker
   (a read-only `/status`-class surface, secret-free) and **consumed** by the
   portal/awareness dashboard and by external monitoring — not reconstructed by
   log-scraping (which couples the monitor to one rotator's log wording).

**Default posture:** a connection whose health cannot be determined renders
`unknown` (not `healthy`) and is treated as **degraded** for alerting — liveness,
like authorization, **fails closed**.

## Consequences

**Positive**

- Closes the entire incident class as a *principle*, not a patch: any future
  rotator (MCP today, Tessera at the v0.6.0 cutover), surface, or provider inherits
  "observable + self-healing-or-loud."
- The awareness dashboard (ADR 0017) and portal (ADR 0016) stop being able to lie;
  truth-by-construction replaces optimistic green.
- Gives the v0.6.0 cutover a measurable exit criterion ("liveness observable + a
  forced-dead session self-heals or pages") instead of "seems fine."

**Negative / cost**

- A first-class health model + surface to build and maintain (the SDD).
- The truthful check costs an upstream exercise per interval; mitigated by caching
  the verdict and sharing it across consumers (ADR 0024's oracle cache).
- `unknown ⇒ degraded` can be noisier than optimistic green — intentionally; a
  false "healthy" is the failure this ADR exists to forbid.

## Rejected alternatives

- **Keep availability implicit (status quo).** Rejected — it is exactly what
  produced two silent outages; the threat model named confidentiality and assumed
  liveness.
- **Process-liveness as health** (pod Ready ⇒ connection alive). Rejected — the pod
  was Ready the whole 5 days; process liveness ≠ session liveness.
- **External monitoring only** (the interim Loki `refresh token dead` scrape).
  Rejected as the *durable* answer — it couples the monitor to one rotator's log
  wording and goes blind across a version/owner change; a broker-emitted health
  signal is owner-agnostic.
- **Optimistic health with a long TTL.** Rejected — a present-but-revoked cookie
  defeats any TTL; only *use* reveals death.
