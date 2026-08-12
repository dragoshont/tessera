# Deterministic Gates

## checks

- `./gates/checks.sh --quick`: PASS on 2026-08-10; config and
	`docs/ui/tessera-design-map.json` are valid JSON.
- `./gates/checks.sh`: PASS on 2026-08-10; production web build and 94 tests.

## backend-checks

- `PATH="/usr/local/share/dotnet:$PATH" ./gates/backend-checks.sh`: PASS on
	2026-08-10; build and 617 tests, additive migrations through v4, Kubernetes render,
	kubeconform 4/4, and deployment secret scan.
- No infrastructure apply or runtime mutation occurred.

## reconcile

- `./gates/reconcile.sh`: PASS with explicit early-adoption skip. No
	token/tokenBuild is configured; mechanical token reconciliation is not claimed.
- `npm --prefix web run build-storybook`: PASS; all continuity states emitted and
	the a11y addon bundled.
- `npm --prefix web run test:e2e`: PASS; 16 tests across desktop and 390px phone,
	including the complete continuity timeline/Why journey.
- `npm --prefix web run lint`: PASS.

## other

- `./harness/validate-run.sh .architrave/runs/20260810-r1-continuity`: PASS.
- `git diff --check`: PASS.
- Editor diagnostics: clean for changed Core, SQLite, Broker, tests, and web sources.
- `dotnet list Tessera.slnx package --vulnerable --include-transitive`: PASS;
	no vulnerable direct or transitive NuGet packages reported.
- `npm --prefix web audit --omit=dev`: PASS; 0 production vulnerabilities.
- Full npm audit residual: one Low advisory in the Windows-only Vite/esbuild
	development server; `npm audit fix` cannot currently advance it within constraints.
