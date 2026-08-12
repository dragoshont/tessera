# Final Deployment Runbook

1. Merge the validated Tessera candidate to `main`; CI publishes `ghcr.io/dragoshont/tessera:sha-<commit>`.
2. Resolve and pin the pushed image digest in a clean private homelab worktree.
3. Deploy Tessera into a dedicated `tessera` namespace with default-deny ingress/egress, LAN TLS ingress, `Recreate`, product PVC and off-node backup destination.
4. Reference existing Key Vault, LiteLLM and isolated RM services through secret references; never copy values into Git.
5. Render and policy-check the complete Flux tree, commit the Tessera-only GitOps change and push.
6. Verify digest, schema v15, readiness, scheduler heartbeat, API JSON, plugin discovery and backup/restore.
7. Perform complete Tessera stack restart and verify durable state before provider authorization.

Rollback reverts the GitOps image/config while retaining non-prunable data/backup storage. Never open a v15 database with an older image or overwrite the live database during restore.