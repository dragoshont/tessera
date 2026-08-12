# Deployment Baseline

The private homelab runs Tessera source `f869d76`, image `ghcr.io/dragoshont/tessera@sha256:582231318e739de0ab6141027209a4140b17c55e1123c79bd87ef117b4c10e91`, from homelab revision `1a584f8`. It is isolated in the dedicated `tessera` namespace with retained RWO data/backup PVs, schema v15, a 1 GiB SQLite page ceiling, daily verified backups and LAN-only TLS at `tessera.hont.ro`.

The namespace-wide `allow-all-egress` policy defeats workload-specific egress restrictions. Final cutover therefore uses a dedicated `tessera` namespace with default-deny policy rather than modifying unrelated `default` workloads.

Flux reports Ready on the merged revision. The pod is `1/1 Running` with zero restarts after rollout; a full Deployment restart recovered the same database and image digest.