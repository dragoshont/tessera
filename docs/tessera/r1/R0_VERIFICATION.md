# Tessera R1 R0 Verification

**Status:** PASS - R1 MAY PROCEED
**Verified:** 2026-08-10

## Repository State

- HEAD: `723aa31514ed840678bb01aa6b316ea0cfd10902`
- Branch: `2.0-beta`
- The R0 implementation and documentation remain uncommitted in the current working tree. The tracked modifications and untracked Kernel, SQLite, test, documentation, and Architrave files match the R0 handoff inventory.
- The current dirty tree was preserved; no R0 change was reverted or silently replaced.

## Baseline And Current Gates

The R0 baseline was 546 backend tests and 73 web tests. Fresh R1-entry verification produced:

| Gate | Result |
|---|---|
| `PATH="/usr/local/share/dotnet:$PATH" ./gates/backend-checks.sh` | PASS - 599 tests; build, migrations, Kubernetes render, kubeconform 4/4, and deployment secret scan passed |
| `./gates/checks.sh` | PASS - production web build and 74 tests |
| `dotnet list Tessera.slnx package --vulnerable --include-transitive` | PASS - no vulnerable direct or transitive package reported |
| `./harness/validate-run.sh .architrave/runs/20260809-tessera-kernel-docs` | PASS |
| `git diff --check` | PASS |

The checks warned that copied Architrave kit assets are version `0.7.0` while the installed plugin is `0.10.3`. This is tooling drift, not a Kernel trust failure; R1 must not overwrite the repository kit implicitly.

## R0 Acceptance Verification

Source inspection confirmed:

- canonical issuer/tenant/subject principals in `Tessera.Core.Kernel`;
- owner-scoped durable evidence, append-oriented observation events, assertions, actions, authorizations, and workflow checkpoints;
- deterministic, sensitivity-bounded context construction;
- replaceable model and versioned capability contracts;
- SQLite v1/v2 migrations, optimistic versions, owner-scoped indexes, and transactional operations;
- atomic authorization consumption and action reservation;
- payload, target, capability, owner, version, and idempotency binding at execution reservation;
- correction, conflict, restart, replay, lifecycle, and hostile-evidence regressions;
- canonical portal/broker trust fixes and iframe source/origin binding.

## Residual R0 Findings

| Finding | R1 disposition |
|---|---|
| Legacy display-keyed policies | Fail closed for tenant-aware users; migrate before live execution. R1 uses synthetic canonical owners. |
| Kernel authorization and live `WriteChallenge` are separate | No external action is in R1. Convergence remains a pre-live gate. |
| Complete DLP, deletion, backup, and restore are not implemented | R1 uses bounded synthetic fixture content and makes no lifecycle guarantee. |
| OIDC portal admins require canonical principal IDs | Unchanged; R1 does not alter admin policy. |
| Kernel SQLite adapter is not composed into the deployed Broker | R1 may compose a local product service/API for development, but deployment/PVC durability is not claimed or changed. |

No unresolved Critical or High R0 trust issue invalidates the read-only, synthetic R1 continuity slice.

## Discrepancies

- The canonical overnight build specification is referenced by the previous run as an external Downloads file; it is not present in the repository and therefore is not a new R1 source of truth.
- The R0 report accurately records 599 backend and 74 web tests. Fresh verification matches those counts.
- The working tree is intentionally dirty because R0 was not committed. Any description of a clean tree would be incorrect.

## Decision

R0 acceptance remains PASS. R1 may safely proceed without weakening R0 semantics, provided it remains provider-neutral, performs no external mutation, preserves candidate/current separation, and does not claim production deletion or deployment durability.