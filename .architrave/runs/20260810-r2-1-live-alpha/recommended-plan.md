# Recommended Plan

## Summary

Operate and harden the existing Alpha in independent slices, validate each immediately, then run full deterministic and adversarial gates. Keep live providers explicitly blocked until real evidence exists.

## Implementation Sequence

1. Baseline and runtime audit.
2. SQLite health plus atomic backup/restore.
3. Active readiness and one-command startup.
4. Safe live verification harness.
5. GitHub identity/scopes/permissions migration.
6. SSE streaming, browser rendering, cancellation, refresh/restart.
7. Product state/recovery UX and responsive evidence.
8. Adversarial review; fix SSE isolation, DLP, SSRF, permission truth, trace recovery, Job health.
9. Full gates, docs, run validation.

## Test Strategy

Focused test after every edit; then full solution, Vitest, lint, production/Storybook, Playwright desktop/390px, Compose/IaC, secret/PII, dependency advisories, diff, runtime health, live harness blocked-state, and independent post-fix judges.

## Rollback / Recovery

All schema change is additive v12. Backup/restore never overwrites. Streaming falls back for non-streaming transports. No branch/reset/clean was used. An operator can stop Tessera and restore a verified isolated SQLite image offline.

## Human Approval Needed

Only live external mutation: exact repository plus `TESSERA_ENABLE_LIVE_WRITE_TESTS=true` and matching confirmation target. Deployment/apply/commit also remain human-controlled.
