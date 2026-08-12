# Deployment Runbook

## Plan

```bash
./scripts/deploy-homelab
./gates/checks.sh
./gates/backend-checks.sh
```

The helper renders and validates manifests but never mutates Git, Flux, or Kubernetes.

## Human-reviewed GitOps changes

1. Build/publish immutable Tessera and Regina Maria MCP image tags from reviewed commits.
2. Copy the validated PVC, data mount, backup CronJob, fixed LiteLLM gateway, and two RM connector settings into `apps/platform/tessera` in the private homelab repository.
3. Pin the same immutable Tessera digest on the Deployment and backup CronJob. The verified template mounts the ConfigMap read-only and has no grants init container or writable grants copy; keep GitOps as the grants source of truth.
4. Add the private registry pull secret to both the Deployment and backup CronJob when the image is not public.
5. For the current MicroK8s host, review `microk8s-hostpath`, 2 Gi capacity per PVC, and Flux prune protection before adding `pvc.yaml`.
6. Add the Google OAuth client ID/callback and a Key Vault reference for its client secret. Never commit the secret.
7. Add a Tessera-dedicated LiteLLM key through the existing secret-management path, or enter it once through Settings.
8. Review the network policy caveat in `SECURITY_REVIEW.md`. The namespace-wide allow-all egress policy makes a narrower Tessera policy non-enforcing; either narrow the global policy or move Tessera to a dedicated default-deny namespace. This is a separate network approval.
9. Remove obsolete direct RM recipe/subscription-key wiring only after both fixed MCP connectors and the Action-token reference validate in the corrected runtime.
10. Commit and push the private GitOps repository. Flux applies automatically.

## Verify

```bash
export TESSERA_BASE_URL=https://tessera.hont.ro
export TESSERA_BEARER_TOKEN=... # enter locally; never commit
./gates/deployed-alpha-checks.sh
```

## Operate

```bash
ssh homelab 'microk8s kubectl -n default get deploy,pod,svc,pvc -l app=tessera'
ssh homelab 'microk8s kubectl -n default logs deploy/tessera --tail=200'
ssh homelab 'microk8s kubectl -n default rollout status deploy/tessera'
```

A restart/reconcile remains a human-approved mutation. Roll back by reverting the GitOps commit to the prior immutable image/config; do not roll back the SQLite file after schema migration without the verified restore procedure.