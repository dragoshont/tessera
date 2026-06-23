# ADR 0024 — Session liveness is the rotator's truth; re-seed is oracle-driven, not timer-driven

- **Status:** Proposed (2026-06-23)
- **Deciders:** maintainer (Dragoș)
- **Relates to:** [ADR 0015](0015-mcp-egress-through-tessera.md) (domain MCP egress
  through Tessera), [ADR 0020](0020-credential-ownership.md) (credential ownership),
  [ADR 0022](0022-apple-caldav-tessera.md) (Apple CalDAV via Tessera),
  the sessionkeeper runtime architecture **A4/A5/A6** (homelab
  `apps/platform/sessionkeeper/ARCHITECTURE.md`), and the public engine spec
  ([sessionkeeper `docs/harvester-spec.md`](https://github.com/dragoshont/sessionkeeper/blob/main/docs/harvester-spec.md)).
- **Implemented by:** homelab **SDD-42** (`docs/sdd/42-rm-session-liveness-loop`).
- **Supersedes the relevant part of:** the optimistic-probe behaviour assumed by
  A6 ("KV staleness tolerable; re-seed is automated") — it was *not* reliably
  automated, because nothing told the re-seeder the chain had died.

## Context

A Regina Maria (RM) session is **one rotating refresh-token chain**. Three real
events trace to a **single** architectural gap:

1. **2026-06-23 — silent death (~5 days).** RM was functionally dead for days while
   monitoring read "healthy." The sessionkeeper harvester's `probe()` is an
   **optimistic `ttl_hint` countdown**: it asserts the success *cookie is present*,
   not that the chain is *valid*. A dead-but-present session looked healthy, so the
   harvester never escalated to a re-seed; meanwhile the rotator (`reginamaria-mcp`
   keep-warm) logged `refresh token dead` every ~10 min, watched by nothing.
2. **Phase-B Tessera cutover — reverted** (`ca148ee` *"restore the reliable keeper —
   RM session went stale on the target"*). When Tessera became the rotator (the
   credential-free v0.6.0 target, ADR 0015/0020), the session went stale and the
   cutover was rolled back to the v0.5.2 MCP keeper. **Same gap:** the new rotator
   (Tessera) knew, the harvester (still the sole browser re-seeder) did not.
3. The **safe machinery already exists and is correct.** The scheduler's
   escalate→`login()` arm is gated by `_LoginBreaker` (`max_logins_per_day` +
   `min_seconds_between_logins` → `NEEDS_HUMAN`), built *explicitly* to stop relogin
   storms that "escalate reCAPTCHA invisible → hard challenge → account flag." RM
   caps logins at ~4/day. The breaker is sound; it is simply **never triggered**,
   because `probe()` decides from a timer, not the truth.

> The component that **knows** the chain is dead (the *rotator* — the MCP in v0.5.2
> per A4, Tessera at the v0.6.0 cutover) and the component that can **fix** it (the
> *harvester*, the sole browser re-seeder per A5) are decoupled, and the harvester
> re-seeds on a **timer**, not on the rotator's verdict. That single gap produced
> both the silent death and the stale cutover.

A4 deliberately made **the rotator the single rotation owner** to avoid the
rotation race (two HTTP-rotators corrupt the chain). That decision is correct and
is **kept**. The fault is not *who rotates* — it is that *liveness is guessed*.

## Decision

**Session liveness is the rotator's ground truth, and re-seed is driven by a real
liveness oracle the harvester queries — never by an optimistic timer. The oracle
interface is owner-agnostic, so the same loop hardens the current MCP-owned
deployment (v0.5.2) and the future Tessera-owned target (v0.6.0) by repointing one
URL.**

| Role | Component | New responsibility |
|---|---|---|
| **Liveness authority** | the **rotator** — `reginamaria-mcp` (v0.5.2, A4) / **Tessera** (at the v0.6.0 cutover) | It exercises the session, so it alone knows `alive / stale / dead / needs-human`. Expose that verdict as (a) a cheap **in-cluster liveness endpoint** and (b) a **Prometheus gauge** (`*_session_alive` / state). |
| **Re-seeder** | the **harvester** (sessionkeeper, sole browser+CDP re-seeder, A5) | `probe()` gains an optional **liveness oracle**: when `settings.liveness_probe_url` is set, the oracle's verdict is ground truth. `DEAD` → escalate to the **breaker-gated** `login()` re-seed (which **parks** the browser, preserving A4). |
| **Watcher** | Prometheus/Grafana | Alert on the rotator's **truthful gauge**, not a log string. |

**Mechanics & invariants**

- **Oracle is a READ.** The harvester *queries* the rotator's liveness; it does not
  write anything new. **A2/A3 hold** — the harvester remains the **sole KV writer**;
  no second writer, no new broad identity.
- **Single rotator unchanged (A4).** The harvester still only seeds/re-seeds and
  *parks*; it never HTTP-rotates. The oracle tells it *when* to re-seed, not *how to
  rotate*.
- **Fail-safe direction.** Oracle says **DEAD** (definitive) → `probe()` returns
  `DEAD`. Oracle **unreachable / unknown** → fall through to the existing cookie/ttl
  assessment (do **not** force a re-seed on a transient oracle blip). The breaker is
  the backstop either way, so a confused oracle cannot storm logins.
- **Confirm before signalling dead.** The rotator reports `dead` only after a
  *definitive, repeated* refresh failure (e.g. N consecutive "refresh token
  expired"), never a single transient 5xx — so a network blip never triggers a
  browser re-login.
- **The irreducible floor.** A *cold* login hits reCAPTCHA → a human (A5's "one
  human step"). The loop's job is to make that **near-never** (the session is kept
  warm) and, when it happens, fire `NEEDS_HUMAN` in **minutes, not days**.

**Default when unset:** if `liveness_probe_url` is absent, `probe()` behaves exactly
as today (timer) — so the change is **opt-in per provider** and cannot regress
providers that have no oracle. RM opts in.

## Consequences

**Positive**

- **Closes the one gap behind both incidents.** The harvester re-seeds on the
  rotator's *truth*, breaker-safe — the 5-day silent death and the stale cutover
  share this fix.
- **Owner-agnostic → hardens now *and* unblocks the strategic cutover.** It hardens
  the deployed v0.5.2 (oracle = MCP) immediately, and turns the reverted Tessera
  cutover (SDD-41 Phase-B) into "**repoint the oracle URL**" rather than a
  re-architecture — removing the *reason* it went stale.
- **Monitoring stops being version-coupled.** The truthful gauge replaces the
  transitional Loki `refresh token dead` scrape (which is v0.5.2-keep-warm-coupled
  and goes blind on v0.6.0).
- **Reuses the consecrated breaker.** No new actor, no new identity, no change to the
  rotation invariant.

**Negative / cost**

- The rotator gains a small **liveness surface** (one endpoint + one gauge). Kept
  cheap, in-cluster, secret-free.
- The harvester does **one extra in-cluster GET per tick**. Negligible.
- One **NetworkPolicy** edge (harvester → rotator liveness). Small, explicit.
- The **fail-safe direction** (oracle-unreachable ⇒ don't force re-seed) is a
  deliberate choice; documented, and backstopped by the breaker.

## Rejected alternatives

- **Keep the optimistic `ttl_hint` probe (status quo).** Rejected — it *is* the bug:
  it asserts cookie-present, not chain-valid, so death is silent.
- **Move rotation into the harvester / sessionkeeper.** Rejected — reverses **A4**
  (the adversarially-reviewed single-rotation-owner decision) and reintroduces the
  token-freshness/propagation problem A4/A5 solved by keeping keep-warm in the
  rotator. Querying the rotator's liveness gets the same truth **without** owning
  rotation.
- **Rotator writes a re-seed marker to KV (the A6 §8 write-back).** Viable and
  already anticipated, but it adds a **second KV writer** (crosses A2/A3) needing a
  new grant + CAS discipline. The oracle achieves the same *trigger* as a **read**;
  defer the write-back to if/when KV-persisted rotation is independently wanted.
- **Re-attempt the full Tessera cutover now (skip the loop).** Rejected — that is
  exactly what went stale (`ca148ee`). Closing the loop is the **prerequisite**, not
  a parallel effort; the cutover follows it, gated by SDD-41.
- **Keep the Loki log-scrape as the durable monitor.** Rejected — coupled to v0.5.2
  keep-warm wording, blind on v0.6.0. A metric is owner-agnostic.
