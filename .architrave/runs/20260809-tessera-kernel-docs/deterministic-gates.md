# Deterministic Gates

## checks

PASS on 2026-08-10 local time after proposal revision.

- production web build passed;
- 10 test files passed;
- 74 tests passed, 0 failed;
- existing non-blocking bundle-size warning remains.

## backend-checks

PASS on 2026-08-10 local time after proposal revision.

- backend build passed;
- 585 tests passed, 0 failed;
- Kubernetes render passed;
- kubeconform: 4 valid, 0 invalid/errors/skipped;
- deployment secret scan passed;
- no apply was run.

## reconcile

Not applicable: no UI, Storybook, token, or generated design change.

## other

- `./harness/validate-run.sh .architrave/runs/20260809-tessera-kernel-docs`: PASS after final proposal revisions and before Claude-family review on 2026-08-10 local time.
- Protected-file baseline captured at `2026-08-09T20:36:35Z`:
	- `docs/tessera/OVERNIGHT_BASELINE.md`: SHA-256 `a8decf83447f71bac27ff4c859b1d6b48ecc501d5be36de72536e2838eb2b748`
	- `docs/tessera/REPOSITORY_MAP.md`: SHA-256 `286980bce7140262fdf250d1668f7597565082a763ba75b23f8ef3f9e56db192`
- Final gate must recompute both hashes and require exact equality.
- Initial source search for Kernel/SQLite contract names under `src/**` and `tests/**`: no matches.
- Concurrent state change during proposal review: five untracked files appeared under `src/Tessera.Core/Kernel/**` and two under `tests/Tessera.Core.Tests/Kernel/**`.
- Observed staged contracts: `PrincipalRef`, `EvidenceRecord`, `ObservationEvent`, `WorkflowCheckpoint`, `AssertionRecord`, `ActionRecord`, and action authorization logic.
- First observed concurrent Core build: FAIL with two `CS0246` errors because `IActionAuthorizationRepository` was referenced but not defined.
- Later concurrent Core build during judge attempt 3: FAIL with one `CA1822` analyzer error in newly appeared `Kernel/Context.cs`; this source changed outside this run between checks.
- A later repository-hook restore observed a newly appeared `src/Tessera.Persistence.Sqlite` project and failed `NU1903` because `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 has a known high-severity vulnerability. This project is concurrent work outside this documentation run.
- Project inventory is volatile. Context, capability, SQLite, test-project, and solution status must be rechecked before final reporting.
## Documentation Implementation Check

PASS after Phase 2:

- all 13 requested top-level artifacts and three ADRs exist and are non-empty;
- both adversarial review files say independent findings are pending;
- `OVERNIGHT_REPORT.md` remains `IN PROGRESS`;
- required product, persistence, atomicity, and no-live-write constraints are present;
- protected baseline/map SHA-256 hashes exactly match the captured values;
- `git diff --check` passed.

## Final Verification Pass

- Final source-only UTC inventory: `docs/tessera/FINAL_INVENTORY.md`, captured `2026-08-09T22:02:19Z` (24 files; generated output excluded).
- `./gates/checks.sh`: PASS; production web build, 74 tests passed.
- `./gates/backend-checks.sh`: PASS; backend build, 599 tests passed, plan-only Kubernetes render/policy passed, deployment secret scan passed.
- NuGet direct/transitive vulnerability audit: no vulnerable packages.
- Documentation allowlist and relative-link checks: PASS.
- Protected baseline/map hash comparison: PASS.
- `git diff --check`: PASS.
- `harness/validate-run.sh`: PASS.
- No deployment apply or runtime mutation was performed.
