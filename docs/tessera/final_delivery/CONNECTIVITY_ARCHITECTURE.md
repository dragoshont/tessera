# Connectivity Architecture

## Goal

Every client reaches one Tessera Home installation. Route changes alter transport only, never state, principal or server identity.

```text
Remote Web / macOS / iOS              Home clients
           |                               |
   Cloudflare HTTPS Tunnel          split DNS / Traefik TLS
           |                               |
           +------ https://tessera.hont.ro-+
                           |
              tessera.tessera.svc:8080
                           |
             one replica -> retained SQLite
```

Native clients may be configured with a distinct TLS-valid local origin before the canonical remote origin. Each candidate must return the exact operator-configured installation UUID; no auth header is sent to discovery.

## Home

`tessera.hont.ro` is currently LAN DNS/TLS and is the canonical Web/macOS/iOS origin. This is already verified from the MacBook baseline. A separate local hostname is optional, not required when split/private DNS makes the canonical name reliable.

## Remote

The selected design reuses the existing two-replica `cloudflared` deployment and Cloudflare-managed ingress configuration. The public hostname maps to `http://tessera.tessera.svc.cluster.local:8080`; the Tessera namespace allows only the labeled cloudflared pods and Traefik on port 8080. The origin remains ClusterIP-only. Tessera's Authentik OIDC boundary still protects product state and APIs.

## Selection And Replay

1. Probe candidate with bounded timeout, manual redirects and no credentials.
2. Require HTTPS, exact six-field descriptor, UUID/API/protocol match.
3. Select first verified healthy route.
4. Reads may retry once on a verified alternate after network/502/503/504 failure.
5. Mutations retry only with the existing idempotency key and byte-identical method/path/body/auth snapshot.
6. Unkeyed/optimistic ambiguous mutations are not replayed; UI refetches canonical state.
7. SSE reconnect reloads persisted Messages rather than inventing durable deltas.

## Cutover Gate

Use the existing `scripts/cf-tunnel-route.sh` helper after the namespace-aware route change is deployed:

```text
add tessera tessera 8080 tessera
```

The helper updates the Cloudflare-managed tunnel route and replaces the public private-IP DNS record with a proxied tunnel CNAME without printing its API token. Verify OIDC callback, SSE streaming, descriptor identity, origin non-exposure, Wi-Fi and cellular before declaring remote access complete.
