# Homelab Reality

Audited 2026-08-12 from the private GitOps repository and live cluster. Secret values were not read or recorded.

## Platform

- Runtime: MicroK8s/Kubernetes reconciled by Flux.
- Ingress: Traefik.
- Remote access: a two-replica `cloudflared` deployment using Cloudflare-managed tunnel configuration.
- Secrets: External Secrets backed by Azure Key Vault.
- Images: GHCR, pinned by immutable digest in private GitOps.
- Tessera: namespace-scoped, stateful, one replica, retained data and backup PVCs, default-deny network policy.

## Reused Pattern

Tessera follows the existing platform service pattern: ClusterIP service, Traefik ingress for local access, Cloudflare Tunnel hostname routed directly to the namespace service, ExternalSecret projection, digest-pinned GHCR image, health probes, PVCs and namespace egress policy. It does not introduce a parallel remote-access system.

## Connectivity Truth

The canonical remote route is:

```text
Client -> Cloudflare -> existing tunnel -> tessera.tessera.svc.cluster.local:8080
```

Local split DNS resolves the canonical hostname through Traefik. Public DNS is now a proxied Cloudflare record and the managed tunnel routes `tessera.hont.ro` to `http://tessera.tessera.svc.cluster.local:8080`. A forced Cloudflare-anycast request returned `server: cloudflare`, a `cf-ray`, the strict descriptor and healthy origin.

## Tailscale Classification

| Occurrence | Classification | Resolution |
|---|---|---|
| Prior `/32` deployment requirement | STALE_ASSUMPTION | Removed from controlling architecture |
| ADR alternative discussion | DOCUMENTATION_ONLY | Retained only as a rejected alternative |
| Admin-portal design references | DOCUMENTATION_ONLY | Non-controlling product precedent |

Tailscale is not required for Tessera remote access.