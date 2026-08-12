# Deployment Architecture

```mermaid
flowchart LR
  Mac[MacBook browser] -->|LAN HTTPS| Traefik
  Traefik --> Tessera[Tessera API + SPA + scheduler]
  Tessera --> DB[(SQLite PVC)]
  Tessera -->|fixed internal /v1| LiteLLM
  Tessera -->|fixed MCP account A| RMA[RM MCP A]
  Tessera -->|fixed MCP account B| RMB[RM MCP B]
  Tessera -->|official HTTPS APIs| Gmail[Google OAuth + Gmail]
  RMA --> KVA[Key Vault session A]
  RMB --> KVB[Key Vault session B]
  Backup[CronJob online backup] --> DB
```

Tessera remains one replica because SQLite and the scheduler have a single-writer topology. `Recreate` rollout prevents two application versions from opening the RWO database during an upgrade. RM MCPs remain the sole rotating session owners; Tessera stores only fixed connector handles and never receives cookies. The browser/login workers remain internal and outside the API process.

The fixed internal transport router permits only configured RM `/mcp` URLs and configured model `/v1/` prefixes to reach private addresses. All other R2 provider traffic retains the public/loopback address guard.