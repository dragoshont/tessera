# SDD-01 — design (file-level diff plan)

Implements [ADR 0025](../../adr/0025-liveness-first-class-invariant.md). The lie is
one line — `PortalService.MapStatus`: `CredentialStatus.Present => "live"`. Presence
is not liveness. This plan threads an honest verdict and surfaces it.

## P1 — backend (`Tessera.Core`)

**`src/Tessera.Core/Portal/PortalService.cs`**
- Make `MapStatus` honest + verdict-aware (and `internal` for direct unit tests):
  ```csharp
  internal static string MapStatus(CredentialStatus status, bool? verifiedAlive) => status switch
  {
      CredentialStatus.Absent     => "absent",
      CredentialStatus.Incomplete => "error",
      CredentialStatus.Present    => verifiedAlive switch
      {
          true  => "live",        // a real verdict confirmed the session works
          false => "dead",        // a real verdict says the session is dead
          null  => "unverified",  // present, but Tessera has NOT confirmed it (honest default)
      },
      _ => "error",
  };
  ```
- `ListConnectionsAsync` (~L330) and `AddConnectionAsync` (~L435): call
  `MapStatus(health.Status, verifiedAlive: null)` and set `LastVerifiedAt: null`.
  The `verifiedAlive` seam stays `null` until P4 supplies a real verdict — so today
  every present-but-unexercised connection honestly reads **`unverified`**.
- The `needs-attention` rollup + the row ordering already key on `Status is not
  "live"`, so `unverified` correctly sorts to the top and counts as attention — no
  change needed there (assert it in tests).

**`src/Tessera.Core/Portal/PortalConnection.cs`**
- Add `DateTimeOffset? LastVerifiedAt = null` (null = never confirmed alive — the
  same honesty as `ScheduleView.LastRotatedAt`). Update the `Status` doc-comment to
  include `unverified` and add a `<param name="LastVerifiedAt">`.

**`tests/Tessera.Core.Tests/Portal/…`**
- Direct `MapStatus` unit test: `Present + null ⇒ unverified`, `Present + true ⇒
  live`, `Present + false ⇒ dead`, `Absent ⇒ absent`, `Incomplete ⇒ error`.
- Update existing projection tests that assert `"live"` to `"unverified"` (they
  encoded the optimistic bug; the corrected truth is `unverified`).
- Assert `ListConnectionsAsync` sets `LastVerifiedAt = null` and that a present
  bundle yields `unverified`; `PersonView.NeedsAttentionCount` counts it.

> **No change to `BindingHealth`/`CredentialResolver`** — presence assessment stays
> exactly as is (secret-free, no upstream call). Only the *interpretation* of
> presence becomes honest.

## P2 — frontend (`web`) — ground in Storybook, values from tokens

**`web/src/components/badges/HealthBadge.tsx` (+ `.stories.tsx`)**
- Add an **`unverified`** variant: a distinct **non-green** (amber/neutral) badge,
  label "Unverified", semantics "present — not confirmed alive". Reuse the existing
  status→token mapping; do not hard-code colors (Architrave tokens are SSOT).
- Confirm/add `dead` + `needs_human` story states render prominently. Add an
  `unverified` story (and a `live` story stays the *only* green one).

**`web/src/components/accounts/ConnectionDrawer.tsx`**
- Surface `LastVerifiedAt`: "Last confirmed alive: **never**" (null) or the
  timestamp — so the drawer explains *why* it's unverified.

**`web/src/components/handoff/StatusPill.tsx`** — if it renders connection health,
add the `unverified` state for parity (else leave; it's the hand-off pill).

**Tests** — `npm --prefix web run test` (vitest): `HealthBadge` `unverified` renders
non-green + the honest copy; `live` is the only green; `dead`/`needs_human` render.

## P3 — Playwright verification (drive the real Storybook)

- Start Storybook (`npm --prefix web run storybook`, `http://localhost:6006`).
- With the Playwright MCP: open the `HealthBadge` stories; assert the `unverified`
  story is **not** green and shows "present — not confirmed alive"; assert `dead`
  /`needs-login` render prominently; assert `live` is the only green. Screenshot each
  into the run artifacts.
- Open the `ConnectionDrawer` story; assert it shows "Last confirmed alive: never"
  for an unverified connection.

## Alternatives (rejected)

- **Keep `Present ⇒ live`** — the bug; the dashboard lies.
- **Probe upstream on list to get real liveness** — breaks secret-free / no-egress-
  on-list; that verdict belongs to the refresher/oracle (P4), cached, not a list.
- **A `verified: bool` flag instead of a first-class `unverified` state** — the UI
  needs a distinct visual state, and `NeedsAttention` already keys on the status
  string; a state is clearer and reuses the existing plumbing.

## Invariants (diff-review checklist)

Secret-free projection (no value); **no upstream call** added to any list path;
provenance honest-or-null; `unknown ⇒ degraded` (fail-closed liveness). Rollback =
`git revert` per phase (P1/P2 additive, opt-in seam).
