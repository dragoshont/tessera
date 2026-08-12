# Tessera Kernel Test Matrix

## Status Legend

- **PASS:** executable coverage passed in the fresh backend gate.
- **SOURCE:** mechanism is visible in source; dedicated fault/adversarial test remains useful.
- **FUTURE:** required before the named future capability, not an R0 completion claim.

## R0 Coverage

| Invariant | Status | Executable evidence |
|---|---|---|
| Stable canonical identity; display hint ignored | PASS | `PrincipalRefTests` |
| Same email across tenants cannot match canonical binding | PASS | `ResolverTests.Canonical_binding_does_not_match_same_email_in_another_tenant` |
| Owner-scoped reads/writes | PASS | `SqliteStatePersistenceTests.Queries_and_writes_are_owner_scoped` |
| Evidence restart durability and bounded excerpt | PASS | state persistence and `DomainSemanticTests.Evidence_rejects_an_unbounded_excerpt` |
| Event/history and correction provenance preserved | PASS | correction persistence and domain semantic tests |
| Conflict is explicit; no last-write winner | PASS | conflict domain/persistence tests |
| Restricted context omitted and recorded reproducibly | PASS | `ContextCapabilityTests` |
| Model adapters replaceable without storage mutation | PASS | `Model_adapters_are_replaceable_without_mutating_context` |
| Invalid action transition rejected; execution differs from verification | PASS | `Action_rejects_invalid_transition_and_keeps_execution_distinct_from_verification` |
| Exact, expiring, one-time authorization | PASS | domain authorization test |
| Approval/payload binding survives restart | PASS | `Authorization_binding_and_consumption_survive_restart` |
| Stale action/workflow versions rejected | PASS | SQLite execution tests |
| Stable idempotency across restart/retry | PASS | SQLite execution tests |
| Empty and v1-to-v2 migration repeatable | PASS | `SqliteMigrationTests` |
| No credential/policy/audit tables or dedicated prompt/model/diagnostic/secret columns | PASS | `Schema_contains_product_state_only` |
| Observation transaction leaves no partial state | PASS | `Failed_observation_transaction_leaves_no_partial_evidence_or_assertion` |
| Deterministic end-to-end fake Kernel scenario | PASS | `KernelEndToEndTests` |
| Caller `confirm` cannot authorize named write | PASS | `CallerBrokerServiceTests.A_write_call_cannot_be_authorized_by_caller_confirmation` |
| Portal ignores client credential key and uses canonical self-owner | PASS | `PortalEndpointsTests` |
| Iframe message requires origin and source | PASS | `LiveViewIframe` tests in web gate |
| Actual invocation payload/target bound to durable action | PASS | payload-swap and execution-reservation tests |
| Hostile evidence cannot self-authorize | PASS | `Hostile_evidence_cannot_self_authorize_or_invoke_capability` |
| Context ID culture-invariant | PASS | `Context_id_is_stable_across_current_cultures` |
| Temporal inversion rejected | PASS | `Assertion_rejects_inverted_temporal_interval` |
| Action insert/transition bypass rejected | PASS | `Store_rejects_non_proposed_insert_and_transition_jump` |
| Atomic correction order | PASS | atomic correction persistence test |
| Canonical raw approval and OAuth/MCP scope | PASS | Egress/OAuth/MCP canonical endpoint suites |

## Future Adversarial Gates

| Gate | Status | Required result |
|---|---|---|
| Fault between approval consume and action reserve | PASS | one SQLite transaction consumes and reserves |
| Prohibited content in generic text/JSON fields | SOURCE | schema guard plus obvious-secret validator; complete DLP remains future |
| Hostile evidence or worker output claims authority | PASS | no authority issued; capability not invoked |
| Trusted broker-to-Kernel issuance | FUTURE | only authenticated policy plus out-of-band approval can issue |
| Cross-user authorization | PASS | canonical owner and exact authorization tests |
| Ambiguous provider timeout/restart | PASS for fake R0 | durable reconciliation; stale replay denied |
| Capability permission/side-effect mismatch | FUTURE | broker policy denies before invocation |
| Fake receipt or verification without execution | PASS | factory and durable transition guards reject bypass |
| Backup, restore, forget, and erasure | FUTURE | fail-closed restore and truthful lifecycle status |
| Real provider read workflow | FUTURE after Phase -1 `GO` | bounded read-only contract passes |

## Fresh Gate Evidence

Final implementation gates: `./gates/checks.sh` passed with 74 web tests. `./gates/backend-checks.sh` passed with 599 .NET tests, Kubernetes render/policy, and deployment secret scan. NuGet reported no vulnerable direct or transitive packages. Final review status is reported separately in [OVERNIGHT_REPORT.md](OVERNIGHT_REPORT.md).