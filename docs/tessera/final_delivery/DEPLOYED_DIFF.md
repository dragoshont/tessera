# Deployed Diff

Last verified runtime remains the frozen baseline; repository changes in this run have not been pushed/reconciled.

| Dimension | Repository intent | Running homelab | Status |
|---|---|---|---|
| Tessera source/image | current tree with descriptor/iOS shared client | source `f869d76`, digest `58223131…c10e91` | DRIFT |
| server descriptor | stable operator-owned UUID in private deployment config | endpoint absent in old image | DRIFT |
| schema | v15, no migration | v15 | MATCH |
| persistence | retained RWO data+backup PVC, 1 GiB ceiling | Bound and restart-verified | MATCH |
| scheduler | one server replica | one healthy replica | MATCH |
| RM MCP | exact v0.5.38, two isolated accounts | digest `b51b7f13…35217a`, healthy | MATCH |
| LiteLLM | fixed internal route | real completion baseline HTTP 200 | MATCH |
| egress | dedicated namespace default-deny allowlist | verified, undeclared Sonarr denied | MATCH |
| reverse proxy/TLS | `https://tessera.hont.ro` | LAN-only Traefik TLS | MATCH |
| remote route | Cloudflare Tunnel → `tessera.tessera.svc.cluster.local:8080` | hostname absent from tunnel at audit start | MISSING |
| health | descriptor + existing health/readiness | existing health/readiness only | PARTIAL |

Required release action: build/publish a new Tessera image, update private GitOps digest/config, review rendered manifests, push/reconcile under human approval, verify descriptor/no-store, schema/PVC/scheduler/egress, restart recovery and all client routes. Until then, deployment status is intentionally not PASS.
