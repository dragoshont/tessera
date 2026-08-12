# Intake

## Understanding

R2.1 operationalizes the existing R2 Alpha. It does not redesign the product. Work includes one-command startup, active health, provider reality, streaming, backup/restore, recovery, truthful UX, live verification, adversarial repair, and evidence. The intentionally dirty R0/R1/R2 tree must remain intact.

## Acceptance Criteria

- preserve R0/R1/R2 gates and user work;
- complete runtime, Chat, Memory, Accounts, Plugins, Actions, Jobs, recovery, backup, and security contracts;
- never represent contract tests as live provider PASS;
- require explicit matching opt-in for external writes;
- no unresolved Critical/High product, architecture, or security finding;
- `ALPHA_DOGFOOD_READY` only after one live model PASS.

## Grounding Sources

- external canonical R2.1 autonomous specification;
- `docs/tessera/r2/**`, architecture and ADRs;
- `architrave.config.json`, `knowledge/{backend,web,yagni}.md`, `gates/rubric.md`;
- current source/tests and R2 baseline report;
- live loopback runtime/status and package advisories.

## Assumptions

- one active Broker/scheduler for SQLite dogfood;
- loopback local sign-in is the ordinary Alpha path;
- missing external credentials block only live checks;
- GitHub CLI auth is not Tessera Account proof;
- no deployment/apply/commit is authorized.

## Blocking Questions

None for internal implementation. External model and GitHub credentials plus a safe write target are the only remaining external inputs.
