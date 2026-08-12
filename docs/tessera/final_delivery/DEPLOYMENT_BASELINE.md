# Deployment Baseline

The private homelab currently runs Tessera `sha-b5c1cc5` in `default`, with `emptyDir` only. `/api/v1/*` returns the legacy SPA fallback. No product PVC or backup CronJob exists. Ingress is LAN-only TLS at `tessera.hont.ro`.

The namespace-wide `allow-all-egress` policy defeats workload-specific egress restrictions. Final cutover therefore uses a dedicated `tessera` namespace with default-deny policy rather than modifying unrelated `default` workloads.

Flux currently applies homelab `main`; deploy from a clean worktree because the existing checkout contains unrelated changes.