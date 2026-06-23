# SDD-01 (Tessera) — Truthful connection health: the portal stops showing green-while-dead

**Status:** DESIGN — ready to execute. **Safe to implement P1–P3** (additive; no
egress on list; secret-free preserved). Implements
[ADR 0025](../../adr/0025-liveness-first-class-invariant.md) (liveness is a
first-class invariant), Tessera side. Input:
[gap analysis](../../research/liveness-ux-oss-gap-analysis.md) rec #1.

**Owner:** Dragoș · **Composes:** engineering-design, adversarial-review,
standards-grounding, repo-memory · **UI source of truth:** the Storybook
(`web/.storybook`, `http://localhost:6006`) + `docs/ui/tessera-admin-portal-ui-spec.md`
+ design tokens (Architrave). **Verified with Playwright.**

---

## 1. Objective (user-visible outcome — RC-15)

> When I look at the Tessera portal, a connection shows **`live`** *only* if Tessera
> has actually **verified** the session works. A present-but-unverified session
> shows **`unverified`** ("present — not confirmed alive", amber, **not green**); a
> known-dead / login-needed one shows **`dead` / `needs-login`**. So the awareness
> dashboard **can never again render green while the session is dead.** Proven by
> rendering the states in the portal (Playwright) + backend/unit tests — not assumed.

## 2. The gap (grounded in code)

`PortalService.ListConnectionsAsync` sets `Status = MapStatus(health.Status)` where
`health = _resolver.AssessBindingAsync(binding)` — a **presence-only** store
assessment (the doc says *"listing connections never makes an upstream call"*,
*"derived from the store's secret-free status"*). So **presence ⇒ `live`**: a
present-but-dead session (cookies in the store, refresh chain revoked) renders
**`live`**. There is no `unverified`/`unknown` state, and `NeedsAttentionCount` +
the row ordering key on `Status is not "live"`. This is the 5-day-silent-death UX
failure, **Tessera side** — the very surface meant to tell the operator the truth
manufactures a false green.

Tessera *already* does this honestly elsewhere: `ScheduleView.LastRotatedAt` is
*"null until Tessera owns rotation — never a fabricated date"*. **This SDD extends
that same honesty to `Status`.**

## 3. Scope / non-goals

- **In scope:** the *honesty* fix — a first-class **`unverified`** state +
  **`LastVerifiedAt`** provenance; presence-only ⇒ `unverified` (**never** `live`
  without a real verified-alive signal); the portal UI renders `unverified` as a
  distinct **non-green** "present, not confirmed alive" state.
- **Non-goals (deliberate):**
  - **Actively probing the upstream on a list** — that would break the secret-free,
    no-egress-on-list invariant. Liveness `unknown ⇒ degraded` is the honest answer;
    *acquiring* a real verdict (the Mode U refresher / ADR 0024 oracle promoting
    `unverified → live/dead`) is a **future P4**, out of this SDD.
  - Changing the rotation/schedule model (already honest).

## 4. Phases (each: implement → adversarial verify → gate green before the next)

| # | Layer | Change | Verify |
|---|---|---|---|
| **P1** | backend (`Tessera.Core`) | Add `unverified` to the status vocabulary + `LastVerifiedAt` provenance; `MapStatus`/projection returns `unverified` for presence-without-verification (never `live` unless a verified-alive signal is supplied); `NeedsAttention`/ordering already treat non-`live` as attention. | `dotnet test Tessera.slnx`; new tests assert presence ⇒ `unverified`, absent ⇒ `absent`, a verified-alive signal ⇒ `live`. Adversarial review. |
| **P2** | frontend (`web`) | `HealthBadge`/`StatusPill`/`ConnectionDrawer` render `unverified` as a distinct **non-green** state with "present — not confirmed alive" + `LastVerifiedAt` ("last confirmed: never/<time>"). Ground in Storybook (add/confirm the `unverified` + `dead`/`needs_human` story states), from tokens. | `npm --prefix web run test` (vitest); Storybook renders the states. Adversarial review. |
| **P3** | verification | Drive the Storybook with **Playwright**: assert `unverified` renders non-green with the honest copy; `dead`/`needs-login` render prominently; `live` only on the verified fixture. Screenshot each. | Playwright pass + screenshots. Adversarial review. |
| 🔭 P4 | future | A real **use-based verdict** (Mode U refresher / ADR 0024 oracle) that promotes `unverified → live/dead`. **Not in this SDD.** | — |

## 5. Acceptance (falsifiable — see verify.md)

1. A binding whose store bundle is **present but unverified** projects `Status =
   unverified` (today: `live`) and `LastVerifiedAt = null`. *(backend test)*
2. A binding with a **verified-alive** signal projects `live` with a non-null
   `LastVerifiedAt`. *(backend test — the seam exists even though P4 fills it)*
3. `absent` / `error` / `seeding` / `needs_human` / `expiring_soon` unchanged.
4. `NeedsAttentionCount` counts `unverified` (it is not `live`). *(backend test)*
5. `HealthBadge` renders `unverified` **non-green** with the honest copy +
   provenance. *(vitest + Playwright)*
6. No code path makes an upstream call during a portal list; no secret value
   appears in any projection. *(diff review + existing tests stay green)*

## 6. Invariants preserved

Secret-free projection (no value); **no egress on list**; honest-or-null provenance
(extend the `ScheduleView` pattern); fail-closed liveness (`unknown ⇒ degraded`,
never optimistic green). Rollback = `git revert` per phase (P1/P2 additive).
