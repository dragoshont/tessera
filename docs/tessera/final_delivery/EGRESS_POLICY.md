# Egress Policy

Tessera runs in a dedicated namespace with default-deny ingress/egress. Explicit egress permits cluster DNS, existing LiteLLM, the two reviewed RM MCP services, and public HTTPS required for OIDC, Gmail, GitHub and Azure Key Vault. Application fixed-origin and connect-time DNS/IP guards remain active.

The old `default/allow-all-egress` cannot provide containment because Kubernetes policies are additive; moving Tessera avoids breaking unrelated workloads.