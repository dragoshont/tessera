# Intake

## Understanding

Implement the provider-neutral FollowUp continuity proof slice end to end. Reuse the
R0 Kernel primitives, add workflow-specific state and additive SQLite persistence,
compose an explicitly local API, and extend the existing portal design language with
Attention, Tracked, Detail/Timeline, Why, and Correct surfaces.

## Acceptance Criteria

1. AC-R1-01: Canonical product state is a workflow-specific `FollowUp`, not a generic ontology.
2. AC-R1-02: `SourceRecord` is provider-neutral and a deterministic local fixture adapter imports it.
3. AC-R1-03: Imported source becomes R0 `EvidenceRecord` and `ObservationEvent` records.
4. AC-R1-04: Deterministic extraction creates a candidate; it never silently creates current state.
5. AC-R1-05: Accepting a candidate creates durable current state and records acceptance evidence.
6. AC-R1-06: Deliverable, counterparty, due date, and completion each retain field-level provenance.
7. AC-R1-07: Provenance includes evidence, source timestamp, parser version, and confidence.
8. AC-R1-08: User correction is first-class evidence and records supersession lineage.
9. AC-R1-09: Accepted corrected context resolves “Monday instead works for it” deterministically.
10. AC-R1-10: Only the minimum accepted FollowUp context is used for incomplete evidence.
11. AC-R1-11: A credible incompatible due date creates explicit conflict; neither value silently wins.
12. AC-R1-12: Conflict resolution is explicit user evidence and preserves both lineages.
13. AC-R1-13: “Sent it to Rowan” resolves through corrected accepted context as a completion candidate.
14. AC-R1-14: Accepted completion becomes current without external execution or live writes.
15. AC-R1-15: Replayed source identity is idempotent and cannot duplicate or overwrite state.
16. AC-R1-16: Stale source evidence cannot resurrect a superseded value.
17. AC-R1-17: Every read and mutation is owner-scoped; cross-owner access returns no state or is refused.
18. AC-R1-18: SQLite migration is additive, repeatable, and prior v1/v2 stores migrate forward.
19. AC-R1-19: Restart preserves candidate/current/conflict/history/provenance distinctions.
20. AC-R1-20: Timeline and Why are source-grounded, ordered, bounded projections.
21. AC-R1-21: Local API exposes owner-scoped attention, tracked detail, Why, import, accept, correct, and resolve operations.
22. AC-R1-22: API authentication uses the existing canonical/dev principal resolution and ignores client owner claims.
23. AC-R1-23: UI provides Attention, Tracked, Detail/Timeline, Why, and Correct with no chat surface.
24. AC-R1-24: Candidate, conflict, and current are visually and textually distinct without color-only meaning.
25. AC-R1-25: Empty, loading, error, and populated UI states are covered using existing components/tokens.
26. AC-R1-26: Full backend/web/IaC/run gates and independent product, architecture, and security reviews have no unresolved Critical/High finding.

## Grounding Sources

- `architrave.config.json`, `knowledge/yagni.md`, `knowledge/learning-loop.md`, `knowledge/web.md`
- `docs/tessera/r1/R0_VERIFICATION.md`, `docs/tessera/r1/CONTINUITY_VERTICAL_DECISION.md`
- `docs/architecture.md`, `docs/adr`, `docs/specs`, R0 Kernel source/tests
- `docs/ui/tessera-admin-portal-ui-spec.md`, repository Storybook stories and web components
- `gates/rubric.md`, configured deterministic gates

## Assumptions

- The user's explicit authorization covers all implementation phases and replaces separate UI/backend sign-off pauses.
- SQLite composition is local/explicit only; no deployment, PVC, backup, or production durability claim changes.
- Synthetic fixture content is bounded non-secret test data.
- Storybook MCP is configured but unavailable as a callable tool in this session; repository stories/static source are the fallback.

## Blocking Questions

None.
