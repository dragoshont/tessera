# Runtime Observer

## Sources Used

- `./scripts/deploy-homelab` plan-only render.
- Repository `deploy/k8s/**` and `docs/tessera/delivery/**`.
- Read-only private overlay inspection at `../homelab/apps/platform/tessera`.
- Read-only private homelab `git status`, branch and HEAD.

## Observed State

- Repository template renders 7 resources; kubeconform: 7 valid, 0 invalid/errors/skipped.
- Corrected image is local only and has not been published.
- Private Tessera overlay has ConfigMap, Deployment/Service, IngressRoute and Kustomization, but no product/backup PVC or backup CronJob.
- Private homelab tree contains unrelated dirty GH Runner/browser work. No file was edited.
- No cluster query, secret read, apply, reconcile, restart, commit or push occurred.

## Mismatches

- Private overlay pins the old stateless image and lacks schema-v15 durable storage/backup resources.
- Verified template has no grants init container; prior runbook text was stale and has been corrected.
- Private-registry pull credentials must cover the backup CronJob as well as the Deployment.
- Restore documentation previously restored under `/backup` but verified `/data`; it now verifies the same isolated `/backup/restore-test.db` path.
- Namespace-wide allow-all egress makes a Tessera-specific egress policy non-enforcing because Kubernetes network policies are additive.

## Human Approval Items

1. Publish the reviewed corrected image and provide its immutable digest.
2. Review a private GitOps diff adding `pvc.yaml`, `backup.yaml`, data/env mounts and connector/model settings.
3. Approve storage class/capacity/prune behavior and the network-policy strategy.
4. Approve secret references/identity configuration; never materialize values in Git.
5. Commit/push the private repo and allow Flux to apply, then authorize runtime/restart verification.
