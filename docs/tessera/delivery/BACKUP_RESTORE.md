# Backup and Restore

Tessera uses SQLite WAL on the `tessera-data` PVC. Never copy the live `.db` file directly.

## Backup

The CronJob runs Tessera's online SQLite backup API daily with `concurrencyPolicy: Forbid`, then verifies integrity and schema before succeeding. Backups are written to the separate `tessera-backups` PVC and pruned after 14 days. They contain product state and external metadata, not provider credential values; credentials remain in Key Vault.

Manual verified backup:

```bash
ssh homelab 'microk8s kubectl -n default create job --from=cronjob/tessera-backup tessera-backup-manual'
```

## Restore test

Run against a new path, never over the live database:

```bash
ssh homelab 'microk8s kubectl -n default exec <backup-pod> -- dotnet /app/tessera.dll restore --backup /backup/<file>.db --output /backup/restore-test.db'
ssh homelab 'microk8s kubectl -n default exec <backup-pod> -- dotnet /app/tessera.dll verify-backup --database /backup/restore-test.db'
```

Delete the isolated restore test only after verification and human review. An actual production restore requires stopping Tessera, preserving the failed DB, restoring to a new file, and changing the deployment path; do not overwrite in place.