# Storage and Retention

The deployed single-replica backend uses SQLite WAL on a non-prunable RWO product PVC with `Recreate` rollout and a hard 1 GiB SQLite page ceiling. Online backups go to a separate retained target and are integrity/schema-v15 verified. The host root is included in encrypted OneDrive restic scope, but retention is not complete until a fresh off-node snapshot and isolated restore are observed.

Credentials remain in Key Vault/secret custody, not the product database. Product backup contains metadata, conversation, memory, Job, Action and Evidence state. Stop-using does not claim physical erasure from retained backups.