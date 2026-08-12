# Deterministic Gates

## checks

PASS. Web build and 103/103 tests. Direct lint, Storybook, and Playwright 26/26 also PASS. Production/Storybook size warnings recorded.

## backend-checks

PASS. Final backend 711/711. Kubernetes plan-only render PASS; kubeconform 4/4 valid; deploy secret scan clean.

## reconcile

PASS-by-skip: tokens/tokenBuild are not configured. No false design-token conformance claim.

## other

- Compose config PASS.
- NuGet advisories: no vulnerable packages.
- npm production audit: 0 vulnerabilities.
- PII/secret scan PASS.
- `git diff --check` PASS.
- editor diagnostics clean for touched files.
- runtime `/readyz`: backend/database/scheduler ready; external setup configuration-required.
- live harness: runtime PASS; model/GitHub BLOCKED_EXTERNAL; write NOT_RUN_SAFE_MODE.
- Architrave copied kit warning: repo 0.7.0 vs installed plugin 0.10.3; not updated during product work.
