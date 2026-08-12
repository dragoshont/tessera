# Tessera Alpha Operator Guide

## Startup

```bash
./scripts/devloop/up
curl -fsS http://localhost:8080/readyz | jq .
curl -fsS http://localhost:8080/status | jq .
```

Expected clean first run: database/scheduler ready; model/plugins-for-owner/Accounts configuration-required.

## Persistence And Migration

Host DB: `.dev/tessera-product.db`. SQLite enables foreign keys, WAL, and 5-second busy timeout. Startup applies additive migrations through v12. Credentials remain in Lowkey/credential custody, not this DB.

## Backup

```bash
./scripts/devloop/backup
# or specify output
./scripts/devloop/backup .dev/backups/manual.db
```

The SQLite online backup is written to a temporary file, integrity-checked, then atomically renamed. Existing destinations are never overwritten.

## Restore

```bash
./scripts/devloop/restore .dev/backups/manual.db .dev/restored/tessera-product.db
```

Verify the isolated runtime. To replace active state: stop Tessera; preserve the active DB; remove active `-wal`/`-shm` only while stopped; atomically move the verified restored DB into place; start and check `/readyz`. The supported restore command refuses the active path and existing destinations.

## Live Verification

```bash
./gates/live-alpha-checks.sh
TESSERA_LIVE_GITHUB_REPOSITORY=owner/repo ./gates/live-alpha-checks.sh
```

Exit 0 means all requested checks passed; 3 means external configuration blocked one or more checks; 1 means failure. Writes require `TESSERA_ENABLE_LIVE_WRITE_TESTS=true` and matching `TESSERA_LIVE_WRITE_CONFIRM_TARGET`.

## Logs And Scheduler

Logs use stable IDs/errors and never intentionally include credentials. `/status` reports scheduler heartbeat/error and product counts. Stale heartbeat after 45 seconds makes product readiness fail.

## Security

Remote model egress resolves through a public-or-loopback connect-time guard. GitHub uses fixed origin. Product content rejects credential-like properties and common token families before persistence. Plugin manifests are SHA-256 pinned.

## Troubleshooting

- model_not_configured: configure Settings;
- account_auth_required: reconnect Account;
- plugins_not_installed_for_owner: sign in and load Plugins once/catalog check;
- scheduler_heartbeat_stale: inspect Broker logs and restart only after preserving state;
- bundle-size warning: known non-blocking Alpha warning.

## Reset

No convenience reset exists. Stop, back up, and manually remove DB plus sidecars only after confirming the path. Kubernetes/production mutation remains outside this guide.