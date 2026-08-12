# Tessera R1 Test Matrix

| Acceptance criterion | Status | Primary evidence |
|---|---|---|
| AC-R1-01 - R0 remains green | PASS | `gates/backend-checks.sh`, `gates/checks.sh`, 617 backend and 94 web tests |
| AC-R1-02 - One continuity aggregate | PASS | Workflow-specific `FollowUp` domain and SQLite v3/v4 persistence |
| AC-R1-03 - At least three revisions | PASS | Initial, correction, schedule change, conflict/resolution, and completion scenario |
| AC-R1-04 - Historical state preserved | PASS | Superseded/conflicted/rejected revisions and assertion history survive restart |
| AC-R1-05 - Correct current state | PASS | One-current-per-field invariant and end-to-end completion assertions |
| AC-R1-06 - Field-level provenance | PASS | Evidence IDs, source time, parser, confidence, correction, and lineage per revision |
| AC-R1-07 - User correction persists | PASS | Correction survives restart, replay, later imports, and completion |
| AC-R1-08 - Conflict is explicit | PASS | Friday evidence creates two conflicted due-date revisions and no current winner |
| AC-R1-09 - Candidate is not truth | PASS | Initial, Monday, and completion extractions require explicit acceptance |
| AC-R1-10 - Hostile source is data | PASS | Fixed grammar rejects unsupported input; no policy/action/capability path exists |
| AC-R1-11 - Current state survives restart | PASS | Current, candidate, and conflict restart regressions |
| AC-R1-12 - Why is source-grounded | PASS | API/UI show exact evidence and lineage; Playwright asserts each consequential chain |
| AC-R1-13 - What changed works | PASS | Ordered timeline persists and is verified in desktop/phone browser flow |
| AC-R1-14 - Prior context changes handling | PASS | Monday and Sent return `NeedsContext` statelessly and resolve from accepted context |
| AC-R1-15 - Model replacement safe | PASS | No model is used; parser version is recorded and accepted history is durable state |
| AC-R1-16 - Provider-neutral persistence | PASS | Canonical tables use source type/native ID/metadata semantics, not provider schemas |
| AC-R1-17 - No graph/vector requirement | PASS | No graph, vector, semantic index, or embedding dependency exists |
| AC-R1-18 - Owner isolation | PASS | Repository and API cross-principal reads return absent/empty |
| AC-R1-19 - Duplicate source replay safe | PASS | Source and operation receipts are payload-bound and return original result version |
| AC-R1-20 - Stale source cannot resurrect | PASS | Older Friday evidence is retained as rejected history without changing current state |
| AC-R1-21 - Deletion/forget semantics documented | PASS | R1 explicitly makes no production erase/backup/restore guarantee |
| AC-R1-22 - UI represents uncertainty | PASS | Candidate/conflict/current/completed text, icons, borders, and accessible states |
| AC-R1-23 - Compounding evaluation exists | PASS | `COMPOUNDING_MEMORY_EVALUATION.md` plus deterministic and browser evidence |
| AC-R1-24 - Product adversary clears | PASS | Final product verdict PASS; no Critical/High/Major blocker |
| AC-R1-25 - Architecture/security clear | PASS | Final architecture and security verdicts PASS |
| AC-R1-26 - Full gates pass | PASS | 617 backend, 94 web, 16 Playwright; build/lint/Storybook/IaC/audits/run validation green |

## Adversarial Regressions

- strict UTC source timestamps and secret-safe source locators;
- immutable, evidence-causal supersession timestamps;
- original result version under source replay fallback;
- candidate and conflict restart provenance;
- changed source payload and operation collision rejection;
- stale evidence rejection and owner isolation;
- honest Why failure, truncation, focus/Escape, reduced motion, and responsive UI;
- authenticated `503` when local continuity storage is not composed.

The discriminating persistence scenario verifies that the corrected deliverable changes
later Monday/completion interpretation. The browser scenario exercises the same journey
at desktop and 390px phone widths.