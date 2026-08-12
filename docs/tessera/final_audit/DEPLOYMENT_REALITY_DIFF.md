# Deployment Reality Diff

Pre-cutover evidence captured 2026-08-12.

| Dimension | Running deployment | Release candidate | Gap |
|---|---|---|---|
| Source/image | source `f869d76`, digest `58223131...c10e91` | current `2.0-beta` tree | Publish and pin new digest |
| Descriptor | route returns SPA HTML | strict `/.well-known/tessera` descriptor | Deploy new image |
| Database | SQLite schema v15 | schema v15, no destructive migration | None |
| Persistence | retained data and backup PVCs | same mounts and one writer | None |
| Scheduler | one healthy server replica | server-owned scheduler | Reverify after restart |
| Plugins | five installed; provider modules healthy | setup descriptors and catalog sources | Deploy and reverify |
| Product state | principals 1, accounts 0, profiles 0, jobs 0, conversations 0 at audit | idempotent model bootstrap | Run authenticated bootstrap |
| Remote route | Tessera tunnel hostname absent | Cloudflare ingress policy/helper prepared | Add managed tunnel route |
| Model secret | existing Kubernetes LiteLLM secret | AKV-backed ExternalSecret | Migrate without printing value |

The stale image explains the native client's fail-closed offline state: route verification correctly rejects the SPA response before sending authentication. Deployment is not considered complete until the descriptor, health, setup state, persistence, scheduler and Cloudflare path are verified live.