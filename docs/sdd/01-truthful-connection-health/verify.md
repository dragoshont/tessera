# SDD-01 — verify (falsifiable checks)

No phase is done until its checks are green. Each phase ends with an **adversarial
review** before the next starts.

## Tooling preflight (P0)

```bash
cd ~/Repo/tessera
dotnet --version            # backend toolchain present
dotnet build Tessera.slnx   # baseline builds before any change
npm --prefix web ci         # frontend deps
npm --prefix web run test   # baseline frontend tests green
npm --prefix web run storybook &   # Storybook on http://localhost:6006 (for Playwright)
```
Expected: build + tests green *before* edits (so a later red is mine).

## P1 — backend honesty

```bash
dotnet test Tessera.slnx --filter FullyQualifiedName~Portal
```
Expected:
- `MapStatus(Present, null) == "unverified"`; `(Present, true) == "live"`;
  `(Present, false) == "dead"`; `Absent == "absent"`; `Incomplete == "error"`.
- A present-but-unverified binding projects `Status == "unverified"`,
  `LastVerifiedAt == null` (the old assertion was `"live"`).
- `PersonView.NeedsAttentionCount` counts the `unverified` connection.
- Whole solution: `dotnet test Tessera.slnx` green (no regression).

## P2 — frontend honesty

```bash
npm --prefix web run test -- HealthBadge
npm --prefix web run test
```
Expected:
- `HealthBadge` `unverified` renders **non-green** with "present — not confirmed
  alive"; `live` is the only green; `dead`/`needs_human` render. Whole suite green.

## P3 — Playwright (the real Storybook)

With the Playwright MCP against `http://localhost:6006`:
- `HealthBadge` `unverified` story: assert text "present — not confirmed alive" /
  "Unverified" and that the badge color is **not** the success/green token.
- `dead` / `needs-login` stories: render prominently.
- `live` story: the only green.
- `ConnectionDrawer` (unverified): "Last confirmed alive: never".
- Screenshot each into `.architrave/runs/<run>/`.

## Gates + acceptance

```bash
bash gates/checks.sh        # the repo's deterministic gate (generate/build/test + token/designMap)
```
The RC-15 proof: **a present session never renders green unless a verdict confirmed
it** — shown in the live Storybook (Playwright) and asserted in backend + vitest
tests, not assumed.

## Invariant review (every phase)

`git diff` shows: no upstream call added to a list path; no secret value in any
projection; provenance null-not-faked; `unknown ⇒ degraded`. Rollback rehearsed via
`git revert`.
