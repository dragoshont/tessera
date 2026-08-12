# Recommended Plan

## Summary

Use `Tessera.Plugin.Abstractions` as the only first-party provider implementation seam. Broker loads modules generically and retains policy, custody, egress, approvals, execution, and evidence. Plugins own provider semantics and lifecycle integration.

## Implementation Sequence

1. Accept ADR 0032 and expand the typed plugin contract.
2. Move the generic manifest loader from Providers to Plugin.Abstractions. Add bounded canonical assembly discovery and atomically join each module to one enabled hash-pinned package with exact ID/version and declared-capability checks.
3. Add generic configuration source, account hooks, endpoint/service hooks, capability factory, tool-definition/binding contracts, registry snapshot, and Broker dispatch/composition.
4. Extract GitHub: `GitHubRestAdapter`; account validation/allowed-repository parsing; list/create capability implementations; Chat/Job schemas and binding. Move its adapter/tool/account tests to `Tessera.Plugins.GitHub.Tests`.
5. Extract Gmail: `GmailRestAdapter`, `GmailOAuthService`, OAuth endpoints, sync/refresh workers; OAuth config/env parsing; account validation; all Gmail capability implementations, MIME/envelope rules, Chat/Job schemas and binding. Move Gmail adapter/OAuth/worker/endpoint tests to `Tessera.Plugins.Gmail.Tests`.
6. Extract Regina Maria: `ReginaMariaMcpAdapter`, runtime, account endpoints, health worker, capabilities; connector config parsing; all physician/service/interval/appointment schemas and Chat/Job binding. Move the full account/capability suite, including exact cancellation/reschedule and action-token verification, to `Tessera.Plugins.ReginaMaria.Tests`.
7. Keep generic `IHttpTransport`, provider egress, model adapter, execution/policy/custody/evidence, and declarative installation persistence in their existing owners.
8. Remove provider config types/parsing from Core and parse legacy keys inside plugins.
9. Add architecture, discovery atomicity, and executable absent/disabled startup tests.
10. Update solution, publish/container files, docs, and run evidence.
11. Run backend and feasible web deterministic gates, then two-family post-review.

## Test Strategy

Build/test the abstraction first. Run `Tessera.Plugins.GitHub.Tests`, `Tessera.Plugins.Gmail.Tests`, and `Tessera.Plugins.ReginaMaria.Tests` immediately after their extraction, followed by the Broker dispatch tests. The RM suite must retain canonical slot receipts, interval/physician/service binding, exact cancellation/reschedule, account isolation, provider verification, and action-token custody. Gmail retains fixed official routes, bounded MIME/thread handling, OAuth state/PKCE/refresh/revoke, sync, and no fake success. GitHub retains route/repository allow-list and create verification.

Discovery tests cover canonical-root escape, symlink, candidate bounds, deterministic ordinal filename ordering, malformed assembly, duplicate module identity, package/module version mismatch, declared capability with the wrong version, undeclared executable capability, disabled installation, and service-registration failure with no partial registry/endpoint/worker publication. Architecture tests inspect project references, assembly references, namespaces/type names, and runtime source identifiers. Startup tests use separate temporary discovery roots for omitted, physically missing, and disabled Gmail/RM modules. Finish with solution build/test and repository backend checks; report unrelated pre-existing failures separately.

## Rollback / Recovery

Each provider extraction is mechanically isolated. A failed focused gate is repaired in that provider slice before the next starts. No database migration, external call, or deployment occurs. Existing dirty work is never reset or reverted.

## Human Approval Needed

The user's explicit request approves all local phases in this plan. Any deploy, restart, reconcile, network mutation, secret access, or external call remains unapproved and out of scope.
