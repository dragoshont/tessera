# Tournament of Options

## Option A — Minimal Safe Fix

Use the approved architecture but add all R2 tables and repositories in one migration and one implementation phase before adapters. Pros: one schema review and fewer interim shapes. Cons: very large rollback/debug surface and late discriminating feedback. Blast radius: all product persistence at once. Durability: high if correct. Verification burden: concentrated and high. Viable, but loses to staged additive slices.

## Option B — Proper Architectural Fix

Incrementally extend the existing Kernel and SQLite store with three ordered cohesive migrations in the persistence phase, preserve credential custody, implement trusted manifests and fixed-route HTTP adapters, and route Chat and Jobs through one coordinator and R0 Actions. Pros: contract-honest, restart-safe, one execution path, bounded trust boundaries, and early per-migration/repository checks. Cons: more ordered migrations and phase bookkeeping. Blast radius: moderate and table-family-contained. Durability: high. Verification burden: high but discriminating. Wins (`r2-spec.md` sections 13-25, 38-50; ADR 0031).

## Option C — Defer / Ask More

Persist docs only and wait for credentials or more design input. Pros: no runtime risk. Cons: explicitly fails the implementation request; credentials are not needed for test-complete production adapters. Blast radius: none. Durability: none. Verification burden: none. Loses.

## Decision Matrix

| Option | Product truth | Architecture fit | Security | Restart durability | Test burden | Result |
|---|---|---|---|---|---|---|
| A | Pass | Pass | Pass with one large review | Pass | High/concentrated | Lose |
| B | Pass | Pass | Pass with boundary tests | Pass | High | Win |
| C | Fail | Neutral | Pass | None | None | Lose |

## Winner

Option B. Apply the YAGNI ladder at reuse-existing-source-of-truth: one product store, one host, one coordinator, existing custody, existing ContextEnvelope, existing Actions, and tiny local adapters for only the two required HTTP integrations.

## Phase 6 UI Tournament — 2026-08-10

- **Patch the provisional pages:** smallest immediate diff, but preserves the array-based client and monolithic placeholder surface. Low durability and high recurrence risk. Loses.
- **Contract-first client plus isolated components, then route composition:** reuses React Query, Radix, TanStack, lucide, current tokens, and backend contracts. Moderate test burden, contained blast radius, and high durability. Wins.
- **Generate a new OpenAPI client/toolchain:** potentially strong schema coverage, but no generator is configured and the new dependency/config surface is not justified by this phase. Loses on YAGNI.
- **Defer for live credentials:** cannot verify remote providers but local contract and UI behavior are fully testable. Loses.

The selected sequence is typed API and tests, isolated components/stories/design map, route composition, journey tests, then deterministic and dual-family semantic gates.
