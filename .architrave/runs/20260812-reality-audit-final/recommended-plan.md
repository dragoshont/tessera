# Recommended Plan

## Summary

Reconcile runtime truth first, implement only engineering-controlled gaps, publish one immutable image, update private GitOps and route through the existing Cloudflare Tunnel, then run live cross-client checks and record honest remaining auth checkpoints.

## Implementation Sequence

1. Preserve/audit worktree, homelab, deployment, DB and providers.
2. Implement descriptor/shared routing, setup/bootstrap and provider readiness.
3. Implement safe catalog adapters, Web/iOS search and explicit disabled installation for exact hash-pinned local packages; keep public metadata Inspect-only.
4. Remove dead controls and validate all primary journeys.
5. Repackage/install macOS and rebuild iOS Release.
6. Pass deterministic and two-family semantic review; fix findings.
7. Publish image, migrate the existing model key to AKV without disclosure, update private GitOps and Cloudflare route.
8. Verify live descriptor/setup/Chat/persistence/restart/cross-client behavior.

## Test Strategy

Full .NET suite; shared client; Web lint/unit/build/Storybook/desktop+phone Playwright; iOS typecheck/Release; Electron unit/package/installed smoke; K8s render/schema; PII/diff gates; adversarial bootstrap/catalog tests; live TLS/descriptor/auth/setup/SSE/restart checks.

## Rollback / Recovery

Keep the previous macOS app, previous GitOps image digest and existing PVC/backup. Cloudflare hostname route can be removed independently. Never roll back retained SQLite data with a destructive command.

## Human Approval Needed

Only provider/user consent checkpoints: Gmail OAuth, each Regina Maria login/MFA and physical-device signing when unavailable. Infrastructure mutation was explicitly authorized by the controlling mandate.
