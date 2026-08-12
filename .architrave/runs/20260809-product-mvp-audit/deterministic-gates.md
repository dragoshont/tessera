# Deterministic Gates

## checks

`./gates/checks.sh`: PASS on 2026-08-09.

- Production Vite/TypeScript build succeeded.
- 10 Vitest files passed.
- 73 web tests passed, 0 failed.
- Known non-blocking warning: main bundle exceeds 500 kB.

## backend-checks

`./gates/backend-checks.sh`: PASS on 2026-08-09.

- 546 .NET tests passed, 0 failed.
- Kubernetes kustomize plan rendered.
- kubeconform: 4 valid resources, 0 invalid/errors/skipped.
- Deployment secret scan passed.

## reconcile

Not run because no design tokens, design map, or UI implementation changed.

## other

`git diff --check`: PASS.

`./harness/validate-run.sh .architrave/runs/20260809-product-mvp-audit`: PASS.

No runtime deployment or external provider mutation was performed.

The gate reported repository Architrave assets at 0.7.0 while the installed plugin is 0.10.3. Updating the kit is unrelated to this audit and was not performed.
