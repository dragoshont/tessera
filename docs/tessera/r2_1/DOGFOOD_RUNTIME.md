# Dogfood Runtime

## Authoritative Path

```bash
./scripts/devloop/up
```

This starts/reuses Lowkey, seeds development-only custody data, materializes `.dev` configuration from committed examples on first run, uses the built SPA, initializes/migrates `.dev/tessera-product.db`, loads the pinned plugin catalog, starts Chat and scheduler workers, and listens at `http://localhost:8080`.

Sign in with the local developer card as `alice@example.com`. Loopback binding is required for development sign-in.

## Runtime Proof

Verified 2026-08-10:

- clean startup banner with `selftest: null`;
- `/readyz`: backend/database/scheduler ready;
- schema version 12;
- model/plugin-for-owner/Account states reported `configuration-required` on clean state;
- `gates/live-alpha-checks.sh`: runtime PASS, external providers BLOCKED_EXTERNAL.

## Persistence

Host dogfood DB: `.dev/tessera-product.db` plus SQLite WAL/SHM sidecars. It survives normal restarts.

Compose uses named volume `tessera-product` mounted at `/data`. Compose is valid but intentionally requires OIDC because its bind is not loopback; it is not the zero-config dogfood path.

## Stop And Reset

Stop Broker with Ctrl-C. Stop Lowkey with `./scripts/devloop/kv-down`.

Reset is destructive and manual: stop Tessera, create a backup, then remove the DB and sidecars. No reset command is supplied because accidental Alpha data loss is worse than convenience.

## Topology Limit

SQLite plus the in-process scheduler supports one active Broker/scheduler instance. Multi-replica scheduler safety is not claimed.