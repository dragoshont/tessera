# Homelab Discovery

## Platform

The target is a single-node MicroK8s cluster managed by Flux from a private GitOps repository. Traefik provides LAN-only wildcard TLS. Tessera is available at `https://tessera.hont.ro`; the ingress excludes `/mcp` and `/v1` while allowing the SPA, `/api/v1`, and OAuth callback.

## Existing services

- Tessera: ClusterIP `:8080`, one replica, old stateless image, no PVC.
- LiteLLM: ClusterIP `:4000`, one replica, existing models and master-key Secret.
- Regina Maria A: isolated ClusterIP MCP service `:8080`, own Key Vault session, keep-warm active.
- Regina Maria B: separate ClusterIP MCP service `:8080`, separate Key Vault session, keep-warm parked pending account-holder login.
- External Secrets and Azure Key Vault already provide secret custody.

## Constraints

- Do not deploy another LiteLLM or RM connector.
- Do not read or copy RM session values.
- Account B authorization is a human checkpoint.
- The default namespace currently has an allow-all egress policy. A Tessera-specific allow-list cannot provide network containment until the global policy is narrowed or Tessera moves to a dedicated default-deny namespace.
- Flux applies a pushed GitOps change automatically; push/reconcile is therefore a human mutation gate.