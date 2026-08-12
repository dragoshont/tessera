# Retained Gate Evidence

Raw or mechanically extracted outputs for final Phase 6 verification. No secret values, provider content, account IDs, or personal identifiers are retained.

- `backend-trx-counters.txt`: per-project `<Counters>` lines from fresh `tessera-reviewed-install.trx` files; totals reconstruct to 786 passed, 0 failed.
- `web-unit.txt`: complete Web unit command output.
- `web-e2e.txt`: complete desktop/phone Playwright output.
- `shared-client.txt`: complete shared-client typecheck/test output.
- `ios-typecheck.txt`: complete iOS TypeScript output.
- `k8s-schema.txt`: complete public K8s render/schema summary.
- `harness.txt`: complete run-artifact validation output.
- `live-reviewed-packages.txt`: redacted live reviewed-package inventory (IDs/version/enabled only), showing why no canonical package was removed to fabricate a live install target.
