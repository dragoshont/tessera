# Storage and Retention

The deployed single-replica backend uses SQLite WAL on a non-prunable RWO product PVC with `Recreate` rollout and a hard 1 GiB SQLite page ceiling. Online backups go to a separate retained target and are integrity/schema-v15 verified. The encrypted OneDrive restic service completed a fresh snapshot on 2026-08-12 covering the host configuration scope that contains Tessera backups. Full disaster-recovery retention is not claimed until a root operator performs an isolated execution-disabled restore.

Credentials remain in Key Vault/secret custody, not the product database. Product backup contains metadata, conversation, memory, Job, Action and Evidence state. Stop-using does not claim physical erasure from retained backups.