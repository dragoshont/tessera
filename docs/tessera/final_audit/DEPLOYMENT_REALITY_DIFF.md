# Deployment Reality Diff

Post-cutover evidence captured 2026-08-12.

| Dimension | Running deployment | Release candidate | Gap |
|---|---|---|---|
| Source/image | source `4e60505`, digest `835f28b2...44bc38` | final reviewed-install release | MATCH |
| Descriptor | strict six-field JSON, `Cache-Control: no-store` | strict `/.well-known/tessera` descriptor | MATCH |
| Database | SQLite schema v15 | schema v15, no destructive migration | None |
| Persistence | retained data and backup PVCs | same mounts and one writer | None |
| Scheduler | one healthy server replica, Ready after pod replacement | server-owned scheduler | MATCH |
| Plugins | five installed; registry Ready after restart | setup descriptors and catalog sources | MATCH |
| Product state | principals 1, healthy model accounts 1, enabled profiles 1, defaults 1, conversations 1 | idempotent model bootstrap and real Chat | MATCH |
| Remote route | Cloudflare proxied DNS/tunnel to Tessera namespace Service | canonical remote route | MATCH |
| Model secret | AKV ExternalSecret synced; projected key hash matches existing live key | server-owned bootstrap credential | MATCH |

The stale-image native failure is closed. The final iOS Release renders `SERVER VERIFIED` and `Tessera Home`. Owner Microsoft sign-in, automatic model bootstrap and real Web Chat pass; provider authorization and authenticated macOS/iOS continuation remain checkpoints.