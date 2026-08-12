# Recommended Plan

## Summary

Implement a provider-neutral FollowUp continuity service over R0 Kernel primitives,
persist its aggregate snapshots and ordered transitions in additive SQLite tables,
compose it only when a local database path is explicitly supplied, and bind the
existing portal client/components to the same owner-scoped contract.

## Implementation Sequence

1. Freeze the 26 criteria, configured shared contract/design map, domain/source/provenance/context contracts, and test matrix.
2. Add domain records, deterministic fixture adapter/extractor, state transitions, and focused Core tests.
3. Add SQLite v3 migration/repository and the full restart/conflict/replay/owner scenario test.
4. Add explicit local Broker composition and owner-scoped endpoints with endpoint tests.
5. Build the isolated `Continuity/FollowUpWorkspace` component/stories for every state,
   run interaction/accessibility/responsive checks, and add the existing component to
   the design map; only then bind the typed client and compose app views/routes.
6. Run all deterministic gates, then product/architecture/security and dual-family semantic reviews; fix every Critical/High finding.
7. Finalize reports, decision log, compounding evaluation, run artifacts, and settled-doc supersession.

## Test Strategy

Start with the discriminating timeline as the behavior oracle. Add focused unit tests
for parser/context/state invariants, persistence migration/restart/owner isolation,
API authentication and stale/replay behavior, and UI state/actions plus empty/error
and non-color status semantics. Include explicit multi-FollowUp targeting, operation
collision, output bounds, malformed values, auth/503/409, response-race, responsive,
focus, and reduced-motion cases. Finish with configured full gates.

## Rollback / Recovery

The migration is additive; prior binaries ignore new tables. Roll back application
code without dropping tables. A destructive rollback requires a database backup and
is outside R1. Local API composition remains disabled unless an explicit path is
provided. No infrastructure apply, runtime mutation, credential, or external account
access is permitted.

## Human Approval Needed

None for this authorized implementation. Deployment, infrastructure apply, runtime
mutation, secrets, external accounts, and live source writes remain prohibited.
