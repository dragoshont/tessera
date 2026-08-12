# Deployment Baseline

The private homelab runs Tessera source `f9ff112`, image `ghcr.io/dragoshont/tessera@sha256:3545c49d83a5aa43c3ab013bed2d804d0e31d8bfbe4f9c2428efc4f80da64c57`, from homelab revision `fb60061`. It is isolated in the dedicated `tessera` namespace with retained RWO data/backup PVs, schema v15, a 1 GiB SQLite page ceiling, daily verified backups and LAN-only TLS at `tessera.hont.ro`.

The namespace-wide `allow-all-egress` policy defeats workload-specific egress restrictions. Final cutover therefore uses a dedicated `tessera` namespace with default-deny policy rather than modifying unrelated `default` workloads.

Flux reports Ready on the merged revision. The pod is `1/1 Running` with zero restarts after rollout; a full Deployment restart recovered the same database and image digest.