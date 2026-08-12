# Runtime Observer

## Sources Used

Loopback `scripts/devloop/up`, `/readyz`, `/status`, safe live harness, Compose config, and build/test output. No cluster mutation or secret access.

## Observed State

- clean startup at `http://localhost:8080` using Lowkey and SQLite;
- database ready, schema v12;
- scheduler heartbeat ready;
- no model, plugin installation for owner, or Account yet: configuration-required;
- product counts zero on clean owner;
- live harness runtime PASS, providers blocked, writes safe-mode.

## Mismatches

Initial devloop omitted CLI `serve` and could not start; fixed. Initial optional self-test had no matching dev grant; removed from default launcher rather than widening policy.

## Human Approval Items

Configure a real model and GitHub Account through Tessera. External write requires exact sandbox target and explicit opt-in. Deployment/apply/commit remain unperformed.
