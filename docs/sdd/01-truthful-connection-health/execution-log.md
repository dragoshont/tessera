# SDD-01 — execution log (append-only)

> Newest at the bottom. Concise evidence, not a transcript.

## 2026-06-24 — design

- ADR [0025](../../adr/0025-liveness-first-class-invariant.md) (liveness invariant)
  + [gap analysis](../../research/liveness-ux-oss-gap-analysis.md) persisted.
- Grounded: `PortalService.MapStatus` maps `CredentialStatus.Present => "live"`
  (the lie); `ListConnectionsAsync` uses the presence-only `AssessBindingAsync`
  (no upstream call); no `unverified` state; `NeedsAttention`/ordering key on
  `Status is not "live"`. Honest precedent: `ScheduleView.LastRotatedAt` is
  null-not-faked.
- Plan: P1 backend honesty (`Present + null ⇒ unverified` + `LastVerifiedAt`),
  P2 frontend (`HealthBadge` `unverified` non-green, grounded in Storybook),
  P3 Playwright verification. P4 (real verdict) is future.

## P0 — tooling preflight — DONE
- dotnet 10.0.300 (global.json match), node 24, web deps present, Storybook 10, gates present.
- Baseline `dotnet build Tessera.slnx` green (0/0) before edits.

## P1 — backend honesty — DONE (code green) · judge: REVISE (frontend contract drift → fix in P2)
- `MapStatus` now `internal (CredentialStatus, bool? verifiedAlive = null)`: Present+null⇒`unverified`,
  +true⇒`live`, +false⇒`dead`; Absent⇒`absent`; Incomplete/Error⇒`error`. The `Present⇒"live"` lie is gone.
- `PortalConnection.LastVerifiedAt` (null = never confirmed alive). Tests updated + `MapStatus` Theory.
- Verified: `dotnet test Tessera.slnx` **429/0**; `gates/backend-checks.sh` **PASS** (test+iac+secret-scan).
- ADVERSARIAL JUDGE (Architrave): **REVISE**. No green-while-dead in the backend (good). **MAJOR:** the
  wire contract changed (`live`→`unverified`/`dead`) but the frontend `ConnectionStatus` union
  (`web/src/data/types.ts`) lacks them and `HealthBadge.VISUALS` is a total `Record` with no fallback ⇒
  `VISUALS[status].icon` THROWS for every present connection; no gate catches it (tsc green on the stale
  union). → crash-while-present. FIX = extend the union + fail-closed fallback (P2; P1+P2 land atomically).
  Minors: this stale log, `format.ts` `needsAttention` omits the new states, rollup can't split amber/red
  (ADR-sanctioned), `LastVerifiedAt`-non-null untested (P4). Nit: implicit `MapStatus` call sites.

## P2 — frontend honesty (+ judge MAJOR fix) — DONE
- Extended `ConnectionStatus` union (+`unverified`, +`dead`) + `Connection.lastVerifiedAt`.
- `HealthBadge`: `unverified` (amber, HelpCircle, non-green) + `dead` (red, XCircle) + a
  **FAIL-CLOSED `UNKNOWN_FALLBACK`** (unknown status → neutral "Unknown", never green, never throw).
  `format.ts` `needsAttention` counts `unverified`+`dead`. Stories + `AllStates` updated. New
  `HealthBadge.test.tsx`.
- Verified: vitest 70/70, `tsc -b && vite build` green (exhaustive `VISUALS` enforced), `gates/checks.sh` PASS.
- Judge re-verdict (post-P2): the P1 MAJOR is CLOSED (no crash, no false-green). NEW MAJOR M1/M2 —
  `ConnectionDrawer` header still said "· verified {lastUsedAt}" (a use-timer is not a verdict) and the
  planned `lastVerifiedAt` provenance was unsurfaced.

## P2b — drawer honesty (judge M1/M2) — DONE
- `ConnectionDrawer`: header now "· confirmed alive {lastVerifiedAt}" ONLY when verified, else
  "· last used {lastUsedAt}" (no false "verified"); added a "last confirmed alive" Health row
  (`lastVerifiedAt` or "never"). New drawer test asserts no false "· verified" + "never".
- Fixtures made honest: alice's Health Portal = `unverified` (was a false `live`); bob = the
  deliberate verified-`live` sample (carries `lastVerifiedAt`). Updated `fixtures.test` /
  `ConnectionDrawer.test` / `smoke.spec` to the corrected truth.
- Verified: vitest 71/71, build green, `gates/checks.sh` PASS.

## P3 — Playwright verification — DONE · judge: REVISE (orphaned live stories)
- `web/tests/health-honesty.spec.ts` (NEW): demo portal → accounts view shows `Health: Unverified`,
  **zero** `Health: Live` (no false-green), **zero** page errors (no crash). Screenshot captured.
- Full desktop suite **7/7 PASS** (health-honesty, smoke ×2, handoff ×2, screenshots ×2).
- Judge re-verdict (post-P2b): M1/M2/bob-honesty CLOSED. NEW MINOR — the fixture swap (alice live→unverified)
  silently orphaned the two `live` stories (`ConnectionDrawer›Live`, `AccountsTable›SingleLive`) via
  `find(status==='live')!`/`filter→[]` ⇒ blank drawer / empty table; earned-green demonstrated nowhere.

## P2c — earned-green coverage (judge minor) — DONE · judge: PASS
- `fixtures.ts`: added single source of truth `liveConnection = find(status==='live' && lastVerifiedAt)!`
  (bob's verified-live). Re-pointed `ConnectionDrawer›Live` and `AccountsTable›SingleLive` to it
  (no more blank/empty render); added a `ConnectionDrawer›Unverified` story.
- New gates close the masking class: `fixtures.test` asserts `liveConnection` defined + `status==='live'`
  + `lastVerifiedAt` truthy (removal ⇒ red gate, not a silent blank story); `ConnectionDrawer.test`
  renders `liveConnection` and asserts the header `/·\s*confirmed alive/i` + the "last confirmed alive" row.
- Verified: vitest **73/73**, `tsc -b && vite build` green, `gates/checks.sh` **PASS**, Playwright **7/7**.
- ADVERSARIAL JUDGE (Architrave): **PASS**. All AC met, zero blockers; backend honest
  (`Present+null⇒unverified`), frontend fail-closed + truthful copy end-to-end, no false-green on any
  reachable surface, secret-free, gates green, Playwright-proven. The orphaned-story masking class is
  closed by a deterministic gate. Accepted deferred future: **P4** (real periodic use-based verdict
  promoting `unverified→live/dead` + a freshness bound so a stale `lastVerifiedAt` decays off green).

## Status — SDD-01 COMPLETE (judge PASS)
- Backend: P1 (429/0). Frontend: P2+P2b+P2c (vitest 73/73, build, gate PASS). Verify: P3 Playwright 7/7.
- Deferred to a future SDD: P4 verdict engine (the only thing standing between `unverified` and an
  *earned, time-bounded* `live`). Tracked in the roadmap.
