# SDD-06 — Mode U: one Tessera RM broker for every consumer (read + booking)

> **Status:** Proposed — design complete, awaiting operator sign-off on the
> booking-confirmation model (§4) before any phase runs against the live RM
> session. **Posture: sign-off** (touches the live RM session + a credential-model
> cutover — see [the SDD index](../README.md#how-to-read-this)).
>
> **Grounding:** [ADR 0015](../adr/0015-mcp-egress-through-tessera.md) (MCP egress
> through Tessera = Mode U), [ADR 0014](../adr/0014-http-injectable-provider-egress.md)
> (inject-by-identity + single session owner), [ADR 0019](../adr/0019-app-integrations-and-user-delegated-actions.md)
> (app integrations / delegated actions), [ADR 0020](../adr/0020-credential-ownership.md)
> (credential ownership), [ADR 0021](../adr/0021-caller-authentication-plane.md)
> (caller plane), [ADR 0023](../adr/0023-phase3-write-confirmation-out-of-band.md)
> (out-of-band write confirmation — **built, 423 tests**), [ADR 0024](../adr/0024-session-liveness-and-oracle-driven-reseed.md)/[0025](../adr/0025-liveness-first-class-invariant.md)/[0026](../adr/0026-single-writer-rotation-lease-and-fencing.md)
> (liveness + single-writer rotation lease — **shipped `sha-ce73605`**).
> This is the **re-attempt of the reverted v0.6.0 proxy cutover** that
> [SDD-05](../README.md) was the prerequisite for.

## 1. Context — the problem this closes

Two chat surfaces today are **both named `reginamaria`** but hit different backends:

| Surface | Backend | Tools | Capability | State |
|---|---|---|---|---|
| **Text chat** | **Tessera** `/mcp` (`mcpServers.reginamaria.url`) | `tessera_*` → `reginamaria_*` recipe | **read-only** (appointments/profiles/slots) | works since the `tessera_call` args fix (`ce73605`) |
| **Voice / realtime** | **reginamaria-mcp** pod (direct) | `rm_*` | **read + booking** | holds the RM credential |

This is a half-finished migration. Costs: the RM credential lives in two planes
(reginamaria-mcp **and** Tessera read it from KV); booking is only reachable on
the credential-holding direct path; a **new consumer app is coming** and there is
no single, safe surface for it to integrate against.

**The liveness foundation that the earlier cutover lacked is now in place:** the
single-writer rotation lease (ADR 0026), truthful use-based health + freshness
decay (SDD-01/P4), and read-through-on-401 (SDD-05, default-off) all shipped. The
reason v0.6.0 "went stale" — uncontrolled rotation ownership — is addressed by
construction.

## 2. Decision

**Make Tessera the single, identity-routed, credential-free RM broker for *all*
consumers, for *both* read and booking — without moving rotation ownership yet.**

1. **Booking through Tessera (config-only).** Add a `reginamaria_book_appointment`
   write tool to the RM recipe. The egress already forwards a write method's args
   as the JSON body ([ProviderEgress.cs](../../src/Tessera.Providers/ProviderEgress.cs)
   §"A write method forwards the args as the JSON body"), and `search_slots` is
   already a POST — so **no broker code changes**; this is a homelab ConfigMap diff
   (recipe tool + grant) plus the confirmation model (§4).
2. **reginamaria-mcp stays the session/rotation owner.** It keeps `keepwarm` + KV
   write-back and remains the *single rotator* under the ADR 0026 lease. Tessera
   injects **`TokenSSO` only** (ADR 0014), **never refreshes** (`SessionRefresher`
   stays unwired). → **no rotation contention → cannot re-introduce the stale
   failure.** Full ADR 0015 §3 ("Tessera owns rotation") is **explicitly deferred**
   to a later, separately-gated phase (P5).
3. **Consumers integrate via the caller plane (ADR 0021).** A consumer forwards the
   signed-in user's OIDC token to Tessera — MCP `tessera_call` at `/mcp`, or HTTP
   `POST /v1/broker` — and a **grant** authorizes the principal + actions. The
   consumer holds **no RM secret**. This is the reusable surface the new app binds
   to (§6).

**Non-goals (this SDD):** retiring reginamaria-mcp; wiring `SessionRefresher`;
per-account multi-user beyond the existing dragos→account-a / manuela→account-b
bindings; restoring the lost domain ergonomics into a credential-free domain MCP
(ADR 0015 shape C) — the recipe + consumer prompt provide "enough" shaping for now.

## 3. Acceptance criteria

1. Tessera exposes **`reginamaria_book_appointment`** (`POST NewAppointment`,
   action `write:appointment`) reachable via `tessera_call` and `/v1/broker`.
2. The **text chat** can read **and** book through Tessera (booking honoring the §4
   model); no consumer-side change beyond Tessera advertising the new tool.
3. **Voice** routes through Tessera (not the direct pod) and can still read + book.
4. A **new consumer** can onboard with: one grant + OIDC token forwarding + a broker
   call — demonstrated end-to-end by a thin reference caller (§6).
5. **No staleness:** reginamaria-mcp remains the sole rotator (ADR 0026 lease green);
   Tessera injects `TokenSSO` only; SDD-01 health stays truthful across the cutover.
6. **Credential custody unchanged:** the RM session lives only in KV and is injected
   only by Tessera/the MCP; it never reaches a consumer or a log (ADR 0020).
7. Every booking is **audited** with the verified principal, and — where §4 requires
   it — gated by an **un-forgeable** human approval (ADR 0023).

## 4. OPEN DECISION (blocking) — the booking confirmation model

RM booking is a write to a **live medical** API. The owner **removed the per-call
confirm gate on reginamaria-mcp** ("it's my account, zero reasons to not let me
book"). But Tessera is becoming a **shared broker a new app will use** — exactly
the confused-deputy threat ADR 0023 defends against. The grant model
(`Grant.StepUpActions`, per-caller) lets us decide this **per consumer**:

| Option | Booking gate | Pros | Cons |
|---|---|---|---|
| **A — un-gated for all** | none (audit only) | matches the owner's reginamaria-mcp decision; zero friction | a prompt-injected/compromised consumer can book without human intent |
| **B — out-of-band challenge for all** (ADR 0023) | portal approve each booking | strongest; already built; un-forgeable | friction the owner disliked; RM would be the first write activated through it |
| **C — hybrid by caller (RECOMMENDED)** | **first-party human** (text/voice, verified `dragos79@`) = **un-gated + audited**; **app/automation callers** = **out-of-band challenge** | honors the owner's choice on his own account *and* keeps the shared broker safe for the coming app; grant-driven, mostly config | two code paths to reason about (but both already exist) |

**Recommendation: C.** It is the faithful translation of the owner's stated intent
*and* the secure default for the new untrusted consumer, expressed entirely through
`StepUpActions` on each caller's grant. **This is the one decision needed before P1
touches live.**

## 5. Phases, postures, and gates

| Phase | Deliverable | Posture | Gate |
|---|---|---|---|
| **P0 — foundation** | Liveness (ADR 0024/0025/0026) + `tessera_call` args fix | done | shipped `sha-ce73605`, rollout verified |
| **P1 — booking recipe** | `reginamaria_book_appointment` recipe tool + `write:appointment` grant (per §4); homelab ConfigMap diff; broker build/test green | **sign-off** | diff reviewed; **operator approves the live apply**; `backend-checks` green |
| **P2 — text booking verify** | Read + confirmed booking through the text chat, end-to-end | **sign-off** | owner books a real slot once (read-back → yes); audit shows it; SDD-01 health stays truthful |
| **P3 — voice → Tessera** | Repoint `REALTIME_MCP_SERVERS` → Tessera; adapt the LibreChat realtime tool-relay + mutating-deny-net to the `tessera_*` surface | **sign-off** (fork + live voice booking) | voice reads + books through Tessera; reginamaria-mcp no longer a consumer path |
| **P4 — new-app onboarding kit** | A documented caller pattern (ADR 0019/0021) + a thin reference caller proving grant + OIDC-forward + `/v1/broker`/`tessera_call` | **sign-off** | the new app reads RM as its signed-in user with no RM secret |
| **P5 — Tessera owns rotation** (ADR 0015 §3) | Wire `SessionRefresher`; retire reginamaria-mcp keep-warm; reginamaria-mcp fully credential-free | **DEFERRED — plan-only** | only after P1-P4 proven stable; separate SDD; the highest-risk credential cutover |

**Sequencing:** P0 ✅ → **P1 (needs §4 sign-off)** → P2 → P4 (unblocks the new app) ‖ P3 (voice, parallelizable) → P5 (deferred).

## 6. The contract consumers bind to (the reusable surface)

A consumer never sees the RM secret. It authenticates **as the signed-in user** and
calls one of:

- **MCP:** `tessera_call(target: "reginamaria", tool: <name>, args: <object>, confirm: <bool>)`
  at `…/mcp`, `Authorization: Bearer <user OIDC>`. (`confirm` is the legacy step-up
  echo; per §4-C, first-party reads/books proceed, app callers get a 409 challenge.)
- **HTTP:** `POST /v1/broker {op:"call", target:"reginamaria", tool, args, confirm}`
  with the user's bearer token (ADR 0021 caller plane).

RM tools after P1:

| Tool | Method | Action | Notes |
|---|---|---|---|
| `reginamaria_list_appointments` | GET | `read:appointments` | `AppointmentStateId=0` (upcoming) |
| `reginamaria_list_profiles` | GET | `read:profiles` | linked dependents |
| `reginamaria_search_slots` | POST | `read:slots` | args → GetIntervals body |
| **`reginamaria_book_appointment`** | **POST** | **`write:appointment`** | args (`intervalId`,`physicianId`,…) → NewAppointment body; gate per §4 |

Onboarding a new app (P4) = (1) register its caller identity (ADR 0021); (2) add a
`Grant(caller, "reginamaria", [actions], onBehalfOf, StepUpActions)`; (3) it forwards
the user OIDC token and calls the broker. No new RM credential, no fork.

## 7. Rollback

Per phase, all reversible: **P1** — drop the recipe tool + grant from the ConfigMap
(one revert commit; reads unaffected). **P3** — repoint `REALTIME_MCP_SERVERS` back
to the direct pod (the pod stays deployed as the instant fallback throughout). The
RM session owner (reginamaria-mcp) is **never** removed in this SDD, so the live
session can always be reached the old way.

## 8. Adversarial notes (pre-judge)

- **Stale regression** (the v0.6.0 failure): prevented by keeping reginamaria-mcp as
  the sole rotator (ADR 0026 lease) + Tessera injecting `TokenSSO` only. P2/P3 must
  re-check SDD-01 health is still truthful after each cutover step.
- **Confused-deputy booking:** §4-C requires un-forgeable approval for app/automation
  callers; first-party human booking is audited (the owner's accepted risk) and is
  constrained because `intervalId`/`physicianId` must come from a prior search.
- **Credential leak:** no consumer holds the secret; Tessera scrubs it from errors
  (existing `_safe_detail`/scrub). P4's reference caller must be checked for no token
  persistence.
- **Wrong-account access:** the act-as principal a caller may assert is grant-bound
  (ADR 0015 §2) — a compromised consumer cannot impersonate another user.
