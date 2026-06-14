# ADR 0018 - Access gateway outside Tessera; Tessera as privileged action broker

- **Status:** Accepted (2026-06-14; implementation phased)
- **Deciders:** maintainer
- **Relates to:** [ADR 0005](0005-identity-first-fail-closed.md) (verified
  identity), [ADR 0009](0009-end-user-identity-propagation.md) (per-call
  delegation), [ADR 0011](0011-identity-provider-sso.md) (Microsoft direct SSO),
  [ADR 0014](0014-http-injectable-provider-egress.md) (credential-injected
  egress), [ADR 0015](0015-mcp-egress-through-tessera.md) (domain MCP egress),
  [ADR 0016](0016-admin-portal.md) (portal),
  [ADR 0017](0017-awareness-dashboard.md) (awareness dashboard).
- **Detailed design:** [service-access adversarial design](../specs/service-access-adversarial-design.md).

## Context

The media and homelab apps raise two similar-looking but different questions:

1. **Browser access:** may this human open a web UI such as Seerr, Sonarr,
   Radarr, Prowlarr, Grafana, Homepage, or the Tessera Admin UI?
2. **Privileged action:** may this human, assistant, MCP, portal, or script
   perform a specific provider action using a hidden upstream credential, such as
   approving a Seerr request, searching Radarr, pausing qBittorrent, reading an
   RM session, or later using Google/Apple credentials?

The first problem is already well served by identity-aware access gateways and
reverse-proxy auth integrations: Authentik, oauth2-proxy, Pomerium, Authelia,
Cloudflare Access, and similar tools behind Traefik ForwardAuth. They own login,
MFA/passkeys, browser sessions, app reachability policy, redirects, and proxy
headers.

The second problem is Tessera's existing center of gravity. Tessera already
validates identity, resolves provider bindings, reads credential bundles from a
store, injects secrets into egress, and records secret-free audit. The current RM
MCP delegation and [ADR 0015](0015-mcp-egress-through-tessera.md) target are the
clearest proof: the domain tool should know the domain, while Tessera knows the
credential, policy, rotation posture, and audit.

If Tessera attempts to become a full identity provider or full browser access
gateway immediately, it duplicates a large amount of Authentik/Pomerium/oauth2-
proxy functionality. If Tessera stays only a credential-backed action broker, the
overlap is small and the fit is organic.

## Decision

Use an external identity-aware access gateway for browser UI access, and keep
Tessera as the credential-backed privileged action broker.

The first deployed shape should be:

```text
Browser -> Traefik -> Authentik/oauth2-proxy/Pomerium/Authelia -> app UI

User/tool/MCP/script -> Tessera -> provider API/worker
```

Responsibilities:

| Component | Owns | Does not own first |
|---|---|---|
| Traefik | TLS, host/path routing, ForwardAuth calls, upstream routing. | Login state or provider action policy. |
| Authentik/oauth2-proxy/Pomerium/Authelia/Cloudflare Access | Human login, MFA/passkeys, browser session, coarse app access. | Upstream provider API keys or semantic action execution. |
| Tessera | Provider connections, hidden credentials, action-level authorization, optional JIT/elevation, audit, domain MCP egress. | First browser SSO platform, generic app catalog, MFA/passkey lifecycle, reverse proxying every request. |
| Upstream apps | Their own app state, native internal auth where needed, domain workflow. | Tessera policy or hidden credential custody. |

Identity modes:

- **Direct Microsoft mode remains first-class** for Tessera product deployments:
  Microsoft/Entra token -> Tessera.
- **Federated homelab mode is preferred when Authentik is introduced**:
  Microsoft login -> Authentik -> Tessera validates Authentik OIDC token.

Tessera must still validate a real caller token at its own API boundary. It must
not blindly trust arbitrary `X-Forwarded-*`, `X-Auth-*`, `Remote-User`, or
`X-authentik-*` headers unless the deployment has an airtight trusted-proxy and
network boundary. Prefer OIDC/JWT validation from Microsoft direct or Authentik.

## Consequences

**Positive**

- Avoids reimplementing mature browser SSO/session machinery inside Tessera.
- Lets Authentik or a similar tool become the homelab identity control plane:
  Microsoft federation, local users, groups, passkeys/MFA, app access policies.
- Keeps Tessera focused on its differentiator: action-level authorization over
  hidden provider credentials.
- Preserves Azure/Microsoft direct mode for product deployments where Azure stays
  first-class.
- Makes the RM MCP delegation, media providers, Google/Apple providers, and
  future domain MCP egress all instances of the same broker pattern.

**Negative / cost**

- Two policy surfaces exist: access-gateway policy for UI reachability, Tessera
  policy for provider actions.
- Subject and group mapping must be explicit when Tessera trusts Authentik rather
  than Microsoft directly.
- Operators must not confuse "may open Seerr" with "may approve a Seerr request
  through Tessera." These are different authorizations.
- Cross-tool auditing is split: gateway audit says who opened which UI; Tessera
  audit says who performed which provider action.

## Rejected alternatives

- **Make Tessera the first identity provider / SSO platform.** Rejected. It would
  duplicate Authentik/Keycloak/ZITADEL/Ory-style identity features and distract
  from Tessera's broker role.
- **Make Tessera the first browser front door / reverse proxy.** Rejected for the
  first slice. Traefik plus existing ForwardAuth/IAP tools already solve this.
  Tessera may later expose a narrow ForwardAuth-compatible authorization endpoint,
  but that is a later product surface.
- **Use only Authentik and remove Tessera.** Rejected. Authentik can decide who
  may open an app; it does not naturally resolve provider credential bundles,
  inject hidden upstream credentials, perform semantic provider actions, and audit
  action-level outcomes for AI/MCP tools.
- **Trust proxy headers as Tessera identity.** Rejected as the default. Header SSO
  is acceptable only behind a tightly controlled proxy path. Tessera should prefer
  validating a token.
