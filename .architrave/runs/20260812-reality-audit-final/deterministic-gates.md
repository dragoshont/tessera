# Deterministic Gates

## checks

PASS: Web build and 105/105 unit tests. Copied-kit staleness warning is unrelated tooling drift.

## backend-checks

PASS after atomic reviewed installation: 786/786 with fresh `tessera-reviewed-install.trx` files, K8s plan/schema 7/7 and deploy secret scan. Direct concurrent bootstrap, custody repair, revoked/conflicting binding, Key Vault logical-name mapping, malicious catalog, atomic install/receipt rollback, HTTP replay/conflict/public refusal and provider-boundary regressions pass.

## reconcile

PASS/SKIP: token build is not configured. SetupCenter and IntegrationSearchPanel now have Storybook stories and design-map entries; Storybook production build passes.

## other

- Shared client 19/19.
- Web Playwright desktop/phone 44/44, including reviewed local installation and public-result refusal.
- iOS typecheck and standalone Release build PASS.
- macOS 7/7 unit, Electron/package/installed smoke PASS; 0 production vulnerabilities.
- PII/diff checks and public K8s schema PASS.
- Focused concurrent bootstrap/malicious catalog and architecture tests PASS after judge fixes.
- CLI example validation PASS with the non-zero public placeholder.
- `harness/validate-run.sh` PASS.
