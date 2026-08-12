# Deployed Diff

Verified runtime after the 2026-08-12 cutover.

| Dimension | Repository intent | Running homelab | Status |
|---|---|---|---|
| Tessera source/image | final reviewed-install release | source `4e60505`, digest `835f28b2…44bc38` | MATCH |
| server descriptor | stable operator-owned UUID in private deployment config | strict JSON/no-store | MATCH |
| schema | v15, no migration | v15 | MATCH |
| persistence | retained RWO data+backup PVC, 1 GiB ceiling | Bound and restart-verified | MATCH |
| scheduler | one server replica | one healthy replica | MATCH |
| RM MCP | exact v0.5.38, two isolated accounts | digest `b51b7f13…35217a`, healthy | MATCH |
| LiteLLM | fixed internal route | real completion baseline HTTP 200 | MATCH |
| egress | dedicated namespace default-deny allowlist | verified, undeclared Sonarr denied | MATCH |
| reverse proxy/TLS | `https://tessera.hont.ro` | LAN-only Traefik TLS | MATCH |
| remote route | Cloudflare Tunnel → `tessera.tessera.svc.cluster.local:8080` | proxied route live | MATCH |
| health | descriptor + existing health/readiness | DB/scheduler/plugin Ready | MATCH |

Deployment, owner setup and real Web Chat PASS. Provider and authenticated macOS/iOS checks remain user/provider checkpoints, not deployment drift.
