# R2 Test Matrix

Status values are `PASS`, `PARTIAL`, `REVISE`, `FAIL`, and `BLOCKED_EXTERNAL`. `BLOCKED_EXTERNAL` is valid only for live credential/endpoint checks, not implementation tests.

| AC | Phase | Required evidence | Status |
|---|---:|---|---|
| AC-R2-01 | 7 | Existing full backend/web/browser gates | PASS |
| AC-R2-02 | 4/6 | Persistent async Chat, Stop/SSE, retry, restart and browser journey | PASS |
| AC-R2-03 | 2/4 | Real OpenAI-compatible adapter tests; live endpoint | BLOCKED_EXTERNAL |
| AC-R2-04 | 4 | Conversation restart test | PASS |
| AC-R2-05 | 4 | ContextEnvelope selection/provenance test | PASS |
| AC-R2-06 | 4 | Remember Evidence/Assertion test | PASS |
| AC-R2-07 | 4 | Correction/history restart test | PASS |
| AC-R2-08 | 4/6 | Why API/UI provenance test | PASS |
| AC-R2-09 | 1/6 | Owner-scoped Account repository/API/UI tests | PASS |
| AC-R2-10 | 2 | GitHub real transport tests; live PAT | BLOCKED_EXTERNAL |
| AC-R2-11 | 1/2/7 | Schema/log/result/context secret scans | PASS |
| AC-R2-12 | 1/2 | Two same-provider account selection tests | PASS |
| AC-R2-13 | 2 | Manifest semver/hash/schema validation | PASS |
| AC-R2-14 | 2/6 | Production catalog from package loader | PASS |
| AC-R2-15 | 2/3 | Availability intersection and dispatch recheck | PASS |
| AC-R2-16 | 2/3/4 | Date/time and GitHub issue list coordinator tests | PASS |
| AC-R2-17 | 3 | Side effect creates durable Action | PASS |
| AC-R2-18 | 3 | External default policy test | PASS |
| AC-R2-19 | 3 | Payload/account/target/version substitution tests | PASS |
| AC-R2-20 | 3 | One-use replay/concurrency test | PASS |
| AC-R2-21 | 3/4 | Result -> Evidence/Event -> coordinator test | PASS |
| AC-R2-22 | 5/6 | Manual Job API/browser test | PASS |
| AC-R2-23 | 4/5 | Reviewed Chat Job proposal test | PASS |
| AC-R2-24 | 5 | One-time schedule test | PASS |
| AC-R2-25 | 5 | Daily/weekday recurrence test | PASS |
| AC-R2-26 | 5 | Restart recovery test | PASS |
| AC-R2-27 | 5 | Unique occurrence plus fencing race test | PASS |
| AC-R2-28 | 5/6 | Run context/calls/accounts/Actions/outputs/Evidence/trace API/UI | PASS |
| AC-R2-29 | 2/5 | Job account grant denial test | PASS |
| AC-R2-30 | 2/5 | Job capability grant denial test | PASS |
| AC-R2-31 | 3/5 | Model output cannot widen grants test | PASS |
| AC-R2-32 | 3/5 | Job WAITING_FOR_APPROVAL, exact restart approval, success/reconciliation and no-retry tests | PASS |
| AC-R2-33 | 2/3/5 | Disable-before-dispatch race test | PASS |
| AC-R2-34 | 2/3/5 | Revoke-before-dispatch race test | PASS |
| AC-R2-35 | 2/4 | Prompt persists through model outage | PASS |
| AC-R2-36 | 2/5 | Run durable failure through model outage | PASS |
| AC-R2-37 | 2/3 | Timeout creates recoverable durable state | PASS |
| AC-R2-38 | 6/7 | Production fixture/static scan | PASS |
| AC-R2-39 | 2/7 | Production adapter/static fake-success scan | PASS |
| AC-R2-40 | 6 | Empty/config/error/partial Storybook/browser states | PASS |
| AC-R2-41 | 6 | Main navigation browser journeys | PASS |
| AC-R2-42 | 7 | Independent security adversary | PASS |
| AC-R2-43 | 7 | Independent architecture adversary | PASS |
| AC-R2-44 | 7 | Independent product adversary | PASS |
| AC-R2-45 | 7 | All deterministic gates | PASS |
| AC-R2-46 | 7 | R2 report Journey A-J table | PASS implementation; live A/C/D/E BLOCKED_EXTERNAL |
| AC-DEV-01 | D1 | Additive/idempotent v17 migration and rollback readability | PASS |
| AC-DEV-02 | D1 | Owner/conversation snapshot list and cross-owner indistinguishable 404 | PASS |
| AC-DEV-03 | D1 | Atomic idempotent task+run creation; changed/concurrent key conflict | PASS |
| AC-DEV-04 | D1 | Server profile resolves direct argv; path/URL/image/env/shell/write rejected | PASS |
| AC-DEV-05 | D1 | Fenced executor success/failure/cancel/restart/unknown-outcome recovery | PASS |
| AC-DEV-06 | D1 | Non-root, non-privileged, no-token/no-hostPath/no-socket/default-deny manifest | PASS plan; live run BLOCKED_EXTERNAL |
| AC-DEV-07 | D1 | UTF-8/control normalization, redaction scan, 32 KiB truncation | PASS |
| AC-DEV-08 | D1 | Durable JobRun output and canonical Conversation system event | PASS |
| AC-DEV-09 | D1 | Web/macOS/iOS loading/empty/running/output/error/blocked/a11y states | PASS automated/layout; VoiceOver manual checkpoint BLOCKED_EXTERNAL |
| AC-DEV-10 | D1 | Plan/policy output, no apply/runtime mutation, dual semantic PASS | PASS |

Development-workspace client validation includes `npm --prefix ios run typecheck`.
The iOS package currently defines no automated test script, so native behavior must
also be covered by the existing Release device/simulator build plus manual VoiceOver,
Dynamic Type, and Reduce Motion checks; this limitation must remain visible in the
delivery evidence rather than being reported as automated test coverage.

## Journey Mapping

| Journey | Phases | Evidence |
|---|---|---|
| A Configure AI and Chat | 2,4,6 | adapter validation, Chat browser/restart; live call may be BLOCKED_EXTERNAL |
| B Remember/correct/Why | 4,6 | API, restart, provenance browser test |
| C Connect Account | 1,2,6 | custody-backed validation UI; live GitHub may be BLOCKED_EXTERNAL |
| D Read capability | 2,3,4,6 | local real capability PASS; GitHub live may be BLOCKED_EXTERNAL |
| E External Action | 2,3,4,6 | fake transport test of production GitHub path; live mutation BLOCKED_EXTERNAL |
| F Create Job from Chat | 4,5,6 | reviewed proposal to ACTIVE and run history |
| G Job survives restart | 5 | exact-once occurrence/fence test |
| H Job approval | 3,5,6 | WAITING, exact approve, coordinator continuation |
| I Plugin disable | 2,3,5,6 | availability/blocked health/dispatch race |
| J Account revocation | 2,3,5,6 | AUTH_REQUIRED/recovery/dispatch race |
