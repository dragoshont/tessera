# Tessera R2 Report

**Status:** IMPLEMENTATION COMPLETE; LIVE VERIFICATION BLOCKED_EXTERNAL

R2 has additive v5-v11 persistence, custody-backed account/model configuration, strict declarative plugins, real OpenAI-compatible/GitHub transports, one coordinated capability/Action runtime, asynchronous recoverable Chat with Stop/SSE, conversational Memory, conversation-scoped grants, and fenced durable Jobs with inspectable context, calls, accounts, Actions, outputs, Evidence, and trace. Implementation and deterministic gates are complete. Only live model and GitHub verification remain blocked by owner-supplied external configuration.

## Capability Status

| Surface | Status |
|---|---|
| Chat persistence/context/model dispatch | PASS implementation; live provider BLOCKED_EXTERNAL |
| Accounts/model custody and validation | PASS implementation; live providers BLOCKED_EXTERNAL |
| Plugins/GitHub/local utility | PASS implementation; live GitHub BLOCKED_EXTERNAL |
| Durable Actions/approvals | PASS — exact approve/edit-as-new/cancel/replay/expiry/reconciliation and post-approval receipts |
| Jobs/scheduler/restart/grants/fencing | PASS — context, tools, approval waits, restart recovery, projections, cancellation, and reconciliation |
| Memory Remember/Correct/Why | PASS — conversational reviewed mutations plus explorer/history/provenance |
| Product API/UI/navigation | PASS deterministic product journeys |
| Real model live verification | BLOCKED_BY_EXTERNAL_CREDENTIALS/ENDPOINT_CONFIGURATION |
| Real GitHub validation/read/create | BLOCKED_BY_EXTERNAL_CREDENTIALS |
| Product adversary | PASS |
| Architecture adversary | PASS |
| Security adversary | PASS |

## Journey A-J

| Journey | Status | Evidence / residual |
|---|---|---|
| A Configure AI and Chat | BLOCKED_BY_EXTERNAL_CREDENTIALS/ENDPOINT_CONFIGURATION | Real account/profile/custody/coordinator path implemented; no configured live endpoint. |
| B Remember Something | COMPLETE | Model-proposed remember/correct Actions, approval, Evidence/history, context reuse, Why, and restart persistence. |
| C Connect an Account | BLOCKED_BY_EXTERNAL_CREDENTIALS | Custody/connect/revoke/reaper tests pass; real GitHub PAT absent. |
| D Read Capability From Chat | COMPLETE implementation | Local and GitHub model tools use atomic coordinator dispatch; live GitHub is BLOCKED_EXTERNAL. |
| E External Action From Chat | COMPLETE implementation | Model tool creates exact Action; edit/cancel/approve, verification, receipts, and Chat continuation pass. |
| F Create Job From Chat | COMPLETE | Reviewed explicit model-only proposal creates durable active Job. |
| G Job Survives Restart | COMPLETE | Durable occurrence, restart lease takeover, unique occurrence, atomic advance, and fencing tests pass. |
| H Job Requires Approval | COMPLETE | Model write tool parks run, exact approval resumes to output/trace, and unknown outcomes reconcile. |
| I Plugin Disable | COMPLETE | UI and atomic read/write dispatch reservations fail closed. |
| J Account Revocation | COMPLETE | Atomic revoke/cleanup intent, custody retry, UI state, and dispatch denial pass. |

## External Configuration Required

For Journey A: an OpenAI-compatible endpoint URL, model ID, and credential entered through Settings/custody. For Journeys C-E: a GitHub fine-grained PAT with `GET /user`, repository metadata/issues read, and issues write permission for one non-production repository, plus that repository in the allow-list. No secret values belong in repository configuration, tests, logs, or this report.

## No-Mutation Statement

No commit, branch, deployment, IaC apply, secret access, live account use, or external mutation is part of this run.
