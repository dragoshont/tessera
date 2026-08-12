# Tessera R1 Decision Log

| Decision | Rationale | Consequence |
|---|---|---|
| Use FollowUp as the R1 proof vertical | Incomplete later evidence gives accepted context an observable job | Appointment is superseded for R1 only; no permanent category |
| Keep FollowUp workflow-specific | Generic Commitment/Situation/Entity/Claim is unearned | Aggregate invariants remain local and testable |
| Reuse R0 Evidence/Event/Assertion/Context | Existing primitives already own provenance and bounded context | No parallel memory substrate |
| Use fixed local SourceRecord fixtures | Proves continuity mechanics without credentials/provider dependence | No live-ingestion claim |
| Keep candidate separate from current | Extraction is not acceptance | User decisions are explicit evidence |
| Treat correction and resolution as evidence | History and Why must survive restart | Prior values and conflicting lineages remain queryable |
| Use optimistic versions plus operation/source receipts | Prevent stale overwrite and retry ambiguity | Exact replay is idempotent; changed payload conflicts |
| Add SQLite v3 tables and v4 source-payload binding | FollowUp needs transactional aggregate ownership and durable replay integrity | Expand-only rollback is code-only; destructive drop requires backup |
| Compose local API only when an explicit path is set | Deployment lacks approved PVC/backup contract | Default is honest 503; manifests are unchanged |
| Keep browser scenario state visibly volatile | Browser automation must not imply canonical persistence | Demo is opt-in, disappears on reload, and is labeled non-durable |
| Bind supersession time to descendant evidence | Later aggregate updates must not rewrite history | Acceptance/correction lineage determines immutable assertion time |
| Reuse existing portal components/tokens | Preserve established design and accessibility language | Storybook precedes route binding |

## Deferred

Provider selection, live ingestion, cloud model, graph/vector retrieval, general
ontology, external execution, production volume/encryption/backup/erasure, and market
validation remain outside R1.
