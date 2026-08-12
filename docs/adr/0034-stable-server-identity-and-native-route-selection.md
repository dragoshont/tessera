# ADR 0034: Stable server identity and native route selection

**Status:** Accepted

## Context

Web, macOS and iOS must use one canonical Tessera server and may reach it over different private network paths. TLS authenticates a hostname, but native clients also need to distinguish the intended household installation from another valid Tessera deployment. Route failover must not duplicate Messages, Actions or Job runs.

## Decision

Each installation receives one operator-owned, non-secret canonical UUID and display name in `serverIdentity`. Tessera exposes a public, cache-disabled descriptor at `GET /.well-known/tessera` containing only product, server ID/display name, server version, API version and route-protocol version.

Native clients accept a route only when normal TLS validation succeeds and the descriptor matches the expected server ID, API version and protocol version. There is no trust-on-first-use, mDNS discovery, certificate bypass, arbitrary endpoint input or server-directed redirect. A missing identity returns `503 server_identity_unconfigured`; old clients remain compatible, while native route-aware clients fail closed.

The homelab uses one canonical hostname, `https://tessera.hont.ro`. Cloudflare Tunnel provides the remote route to the in-cluster Tessera Service. Local split DNS may route the same hostname through Traefik; route labels are diagnostic client state, never different servers or canonical stores.

Read requests may retry once on a verified alternate path. Mutations may retry only when they carry the same existing idempotency key, byte-identical method/path/body and the same authentication snapshot. Optimistic-version-only writes and ambiguous unkeyed outcomes are reconciled by refetching canonical state rather than replayed. SSE reconnects may lose provisional token deltas but reloads the final persisted Message and never duplicates a durable event.

## Consequences

- No data migration or provider boundary change.
- Server identity rotation intentionally requires client re-pairing.
- Diagnostics may persist route, latency, versions and last success locally, but never canonical product state or provider credentials.
- Cloudflare Tunnel publication must preserve OIDC, TLS, streaming and the namespace default-deny policy.

## Rejected alternatives

- A new Tailscale subnet/Serve/Funnel path: duplicates the existing Cloudflare Tunnel remote-access platform.
- mDNS plus certificate pinning: adds discovery and certificate-rotation complexity.
- Desktop-only routing: duplicates native logic and leaves iOS inconsistent.
- Server-discovered route URLs: exposes topology and creates redirect trust problems.