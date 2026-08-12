# Storage and Retention

The deployed single-replica backend uses SQLite WAL on a non-prunable RWO product PVC with `Recreate` rollout. Online backups go to a separate backup target and are integrity/schema verified. Homelab hostPath on the same node is not sufficient disaster recovery; the deployment must copy backups off-node before retention is called complete.

Credentials remain in Key Vault/secret custody, not the product database. Product backup contains metadata, conversation, memory, Job, Action and Evidence state. Stop-using does not claim physical erasure from retained backups.