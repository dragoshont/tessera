# Spec — Identity & Azure (Entra) setup

> Status: **draft** (design phase). Records the Microsoft Entra setup that backs
> per-user delegation ([ADR 0009](../adr/0009-end-user-identity-propagation.md)) and
> the secretless store ([ADR 0003](../adr/0003-credential-store-pluggable.md)), per
> the IdP decision in [ADR 0011](../adr/0011-identity-provider-sso.md).
>
> The Infrastructure-as-Code lives in [`deploy/azure/entra/`](../../deploy/azure/entra/).

## What Entra gives us

Two distinct Entra **application** objects, both free-tier:

1. **Chat OIDC app** — LibreChat's "Sign in with Microsoft" + the token reuse that
   forwards each user's signed token to the Tessera MCP.
2. **Tessera workload identity (federated credential)** — lets the Tessera broker
   read Key Vault **without a client secret** (Workload Identity Federation), per
   [ADR 0003](../adr/0003-credential-store-pluggable.md).

```mermaid
flowchart LR
    U["user (personal or work Microsoft account)"] -->|OIDC sign-in| LC["LibreChat (chat OIDC app)"]
    LC -->|OPENID_REUSE_TOKENS forwards the user's signed token| MCP["Tessera MCP server"]
    MCP -->|verifies subject_token| BRK["Tessera broker"]
    BRK -->|federated identity (no secret)| KV[("Azure Key Vault")]
```

## 1. Chat OIDC app registration

| Setting | Value | Why |
|---|---|---|
| `signInAudience` | `AzureADandPersonalMicrosoftAccount` | a person who **never used Azure** can sign in with an existing **personal** Microsoft account (live/outlook); work/school accounts also work |
| Redirect URI | `https://chat.<domain>/oauth/callback/openid` | LibreChat's OIDC callback |
| Expose an API + scope | `api://<app-id>/access` (`.default`) | required for `OPENID_REUSE_TOKENS` + the **on-behalf-of** flow so the forwarded token has an audience Tessera can verify |
| ID token / claims | `openid profile email` (+ `offline_access` for refresh) | the `subject_token` payload |
| MFA | required (Conditional Access if licensed, else security defaults) | full-security default |

LibreChat env (set from the registration outputs):

```bash
OPENID_REUSE_TOKENS=true
OPENID_ISSUER=https://login.microsoftonline.com/<tenant-id>/v2.0
OPENID_CLIENT_ID=<app-id>
OPENID_SCOPE="api://<app-id>/.default openid profile email offline_access"
# Google intentionally NOT configured (ADR 0011)
```

> **Personal-account caveat.** With `AzureADandPersonalMicrosoftAccount`, `appRoles`
> aren't honored for consumer (MSA) users at runtime, and the on-behalf-of token
> story is cleaner for **tenant** users. For the **medical / high-isolation** user,
> add them as a **member or B2B guest in the workforce tenant** so delegation and
> roles are unambiguous ([ADR 0004](../adr/0004-tenancy-and-isolation.md)).

## 2. Tessera workload identity (no client secret)

Replace the current Key Vault **client secret** with **Workload Identity
Federation**: federate the broker's Kubernetes ServiceAccount token → Entra → Key
Vault. Nothing long-lived to leak ([ADR 0003](../adr/0003-credential-store-pluggable.md)).

- An app registration (or user-assigned managed identity) for the Tessera broker.
- A **federated identity credential** trusting the cluster's OIDC issuer + the
  broker's ServiceAccount subject.
- A Key Vault **RBAC** role assignment (least privilege: read the secrets it needs;
  envelope-key use for per-tenant decrypt).

## 3. GitHub (deferred)

GitHub user login is **OAuth2-only** (no OIDC/SAML), so Entra cannot federate it
directly. It is **out of scope for iteration 1** ([ADR 0011](../adr/0011-identity-provider-sso.md)).
If wanted later, introduce an OIDC **broker** (Authelia/Keycloak) that federates
GitHub upstream and issues one OIDC token — at which point Entra (or the broker)
becomes the single issuer Tessera trusts. That is the *only* reason to add the
broker tool.

## 4. Security defaults (full-security posture)

- **MFA required**; short token lifetimes.
- **Least-privilege** API permissions and Key Vault RBAC.
- **No long-lived client secret** where a **federated credential** (or certificate)
  works instead.
- Key Vault hardening from [ADR 0003](../adr/0003-credential-store-pluggable.md):
  firewall + Private Endpoint, soft-delete + purge protection, audit logging +
  Defender for Key Vault.

## Open questions

- Exact Microsoft Graph Bicep resource shapes for the federated identity credential
  (child of the application) — verify against the
  [Graph Bicep reference](https://learn.microsoft.com/graph/templates/) at deploy
  time; the `deploy/azure/entra/` template carries `az`-CLI fallbacks for anything
  not yet first-class in Bicep.
- Whether to model household members as workforce-tenant **members** vs personal
  MSA — pick per user (sensitive users → tenant members).
