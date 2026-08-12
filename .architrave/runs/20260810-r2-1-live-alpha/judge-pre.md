# Judge Gate 1

## Verdict

REVISE / FAIL before fixes.

## Findings

- GitHub provider scopes drifted from canonical permissions; fine-grained write was inferred.
- restore overwrite was unsafe.
- transient SSE was not owner-bound and had reader-owned cleanup.
- remote model endpoints could reach private networks.
- unlabeled credential formats could persist in provider results/Evidence.
- interrupted read traces could block Chat/Job recovery.
- streaming event name drifted from API contract.
- dependent Job health, plugin configuration truth, recovery copy, and responsive evidence were incomplete.

All findings were reproduced against current code and fixed before final gates.
