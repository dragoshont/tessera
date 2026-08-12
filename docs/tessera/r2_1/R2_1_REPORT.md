# Tessera R2.1 Report

## Status

`INTERNALLY_READY_EXTERNAL_VERIFICATION_BLOCKED`

## Executive Summary

R2.1 crossed the internal-product boundary: the supported dogfood command now actually starts the complete Alpha; runtime health is active and truthful; SQLite backup/restore is supported and verified; Chat streams real OpenAI-compatible SSE; GitHub identity/permissions are provider-grounded; Jobs recover interrupted traces and reflect dependency health; and setup/recovery UX is product-facing.

No live model or Tessera GitHub credential was available. The implementation therefore cannot be labeled `ALPHA_DOGFOOD_READY`.

## Starting State

Branch `2.0-beta`, HEAD `723aa31514ed840678bb01aa6b316ea0cfd10902`, intentionally dirty R0/R1/R2 tree. Baseline: backend 672, web 100, Playwright 26; external model/GitHub blocked.

## What Changed

- fixed authoritative `scripts/devloop/up` missing `serve` command and removed ungranted optional self-test;
- active database/scheduler/model/plugin/Account status;
- SQLite migration v12 provider identity/scopes;
- verified atomic online backup/isolated restore CLI and scripts;
- true bounded SSE streaming, tools, cancellation, refresh/restart recovery;
- owner-bound transient stream retention and canonical text events;
- GitHub stable identity, classic/fine-grained permission evidence, canonical availability;
- origin-aware public-or-loopback model SSRF guard and expanded recursive DLP;
- atomic Account permission/binding rechecks for reads and approved writes;
- normalized plugin capability DTOs so real Account types are selectable;
- completed read replay and interrupted Chat/Job trace recovery;
- dependent Job health projection;
- Accounts/Plugins/Jobs/recovery/responsive UX hardening;
- safe live-provider harness and all required R2.1 documents.

## What A User Can Do Now

Start Tessera in one command, configure a real model without code edits, use streaming durable Chat, explicitly Remember/Correct/Why, connect GitHub with truthful identity/permissions, inspect/disable Plugins, create and grant durable Jobs, approve exact Actions, inspect Activity, restart safely, and back up/restore isolated state.

## Runtime

`./scripts/devloop/up` verified clean at `http://localhost:8080`. Database and scheduler READY; missing external setup reported configuration-required. SQLite schema v12.

## Live Model

### Implementation

OpenAI-compatible probe, SSE Chat/tools/continuation, bounds, cancellation, errors, SSRF guard, DLP, durable final messages.

### Contract Verification

PASS.

### Live Verification

`BLOCKED_EXTERNAL`.

## Accounts

### GitHub

Implementation PASS. Stable provider ID/login and scopes persist separately. Fine-grained tokens receive read only after repository proof; write is never inferred.

### Other Integrations

Not added in R2.1.

## Chat

Streaming, persistence, retry, Stop, refresh/restart, tools, approvals, Evidence, owner isolation: PASS internally.

## Memory / Continuity

Remember/correct/Why/restart/current-state context: PASS internally. Real-model influence: BLOCKED_EXTERNAL.

## Plugins / Capabilities

Pinned catalog, installed/enabled/configuration/readiness UI, canonical availability and dispatch: PASS.

## Actions / Approvals

Exact payload/account/plugin/capability/target/expiry, one-use authorization, verification/reconciliation: PASS.

## Jobs / Scheduler

Schedules, recurrence, pause/resume, lease/fence, restart, interrupted trace recovery, grants, approvals, outputs/Evidence, health polling/projection: PASS for single-active topology.

## Restart / Recovery

Conversation, Memory, Job, pending Action, Account metadata, interrupted read trace: PASS.

## Backup / Restore

Online consistent backup, integrity verification, atomic publication, isolated restore, overwrite refusal, representative state: PASS.

## Product UX

First-run, provider setup, recovery, operational states, desktop/390px screenshots: PASS internally.

## Dogfood Journeys

Internal journeys PASS. Real Chat, GitHub connect/read, and external Job are BLOCKED_EXTERNAL. Write is NOT_RUN_SAFE_MODE.

## Security Adversary

PASS after owner-bound SSE, expanded DLP, fine-grained permission, and model SSRF fixes.

## Product Adversary

PASS internally after Job health, recovery copy, plugin state, and responsive evidence fixes.

## Architecture Adversary

PASS after trace recovery/replay and canonical capability/streaming contract fixes.

## Full Test Results

- backend 711/711;
- web 103/103;
- Playwright 26/26;
- lint/build/Storybook PASS;
- Compose, Kubernetes render (4), kubeconform (4 valid), Architrave checks, PII/secret, diff PASS;
- NuGet vulnerable packages: none;
- npm production vulnerabilities: 0.

Known warning: web and Storybook large chunks.

## External Blockers

No model endpoint/model credential configured through Tessera. No Tessera GitHub credential/repository configured. No safe write target/opt-in.

## Known Limitations

Single active scheduler; process-local transient streaming; no permanent backup erasure claim; external provider latency/usefulness unmeasured; bundle-size warning.

## Decisions Made

See `R2_1_DECISION_LOG.md`.

## Files Changed

R2.1 touched Core product validation/models; SQLite migration/store/traces/Jobs; provider transport/adapters; Broker host/Chat/scheduler/endpoints; CLI; plugin manifests/catalog; devloop/live gates; web API/pages/components/tests; R2.1 docs and run artifacts.

## Commits

None. No commit, branch, reset, deployment, or external mutation was performed.

## Exact Human Verification Commands

```bash
./scripts/devloop/up
./gates/live-alpha-checks.sh
TESSERA_LIVE_GITHUB_REPOSITORY=owner/repo ./gates/live-alpha-checks.sh
./scripts/devloop/backup
```

To verify a safe write, add the exact matching target and explicit write opt-in documented in `LIVE_GITHUB_VERIFICATION.md`.

## Alpha Scorecard

Runtime/startup, persistent Chat, model contract, Memory, correction/Why, Accounts UX, GitHub implementation, plugin lifecycle, capability policy, Actions, Jobs, recurrence, restart, scoped grants, pending approval, backup/restore, no production mocks, adversaries, backend/web/browser/security gates: PASS.

Live model, GitHub connection/read: BLOCKED_EXTERNAL. Safe external write: NOT_RUN_SAFE_MODE. Dogfood usable with a real model: BLOCKED_EXTERNAL.

## Recommended Next Phase

Perform the tiny external verification checklist first. Then use Tessera for real work and let observed friction, latency, Memory relevance, and Job usefulness determine the next feature phase.