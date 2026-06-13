# Entra setup (Infrastructure-as-Code)

Provisions the Microsoft Entra objects Tessera + the chat need. Decision context:
[ADR 0011](../../../docs/adr/0011-identity-provider-sso.md) ·
spec: [identity-azure-setup.md](../../../docs/specs/identity-azure-setup.md).

> **Status: design-phase template.** Reviewed against Microsoft Learn but **not yet
> deployed against a live tenant.** Treat as a starting point; verify resource
> shapes against <https://aka.ms/graphbicep> before applying. `az` CLI fallbacks are
> given below for anything not yet first-class in the Graph Bicep extension.

## What it creates

1. **Chat OIDC app** (`tessera-chat`) — "Sign in with Microsoft" for LibreChat,
   `signInAudience = AzureADandPersonalMicrosoftAccount` (personal Microsoft
   accounts work with **zero Azure onboarding**), plus an exposed API scope so
   `OPENID_REUSE_TOKENS` + on-behalf-of yields a verifiable `subject_token`.
2. **Tessera broker app** (`tessera-broker`) + a **federated identity credential**
   so the broker reads Key Vault **without a client secret** (Workload Identity
   Federation, [ADR 0003](../../../docs/adr/0003-credential-store-pluggable.md)).

**Google is intentionally not configured. GitHub is deferred** (OAuth2-only, can't
be an Entra IdP without a broker — [ADR 0011](../../../docs/adr/0011-identity-provider-sso.md)).

## Prerequisites

- Azure CLI with the **Bicep** + **Microsoft Graph** support, signed in to the
  target tenant (`az login`), with rights to create app registrations.
- The cluster's **OIDC issuer URL** — **only** if you want workload-identity
  federation. For a **private / homelab cluster Entra can't reach, omit it**: the
  broker keeps its working service-principal secret and no federated credential is
  created.

## Deploy — two paths, same Bicep

Both apply the identical `main.bicep` (declarative IaC). Pick by context.

### A) Pipeline (reusable, product-grade)

[`.github/workflows/deploy-entra.yml`](../../../.github/workflows/deploy-entra.yml)
runs this Bicep via **Azure OIDC** (no stored secret) on `workflow_dispatch`. It
needs a **one-time human bootstrap** (a deployer app + admin-consent) documented at
the top of that workflow — an irreducible step for *any* identity IaC. After that,
deploys are pipeline-driven and auditable.

### B) Direct apply (fastest for a household — this *is* the IaC)

```bash
az deployment sub create \
  --location westeurope \
  --template-file main.bicep \
  --parameters chatDomain=chat.example.com
# (add clusterOidcIssuer="https://<issuer>" ONLY for a publicly-reachable cluster)
```

Then grant consent (the human gate) and wire LibreChat from the outputs:

```bash
az ad app permission admin-consent --id <chatAppId output>   # or the portal button

OPENID_REUSE_TOKENS=true
# Personal Microsoft accounts (gmail-backed) sign in via /common:
OPENID_ISSUER=https://login.microsoftonline.com/common/v2.0
OPENID_CLIENT_ID=<chatAppId output>
OPENID_CALLBACK_URL=/oauth/openid/callback     # LibreChat's real OpenID callback
OPENID_SCOPE="api://<chatAppId>/.default openid profile email offline_access"
# (client secret created out-of-band or via cert; never commit it)
```

## `az` CLI fallback — federated identity credential

If the Bicep `federatedIdentityCredentials` child resource isn't yet supported in
your Graph Bicep version, create it directly (this is the verified path):

```bash
az ad app federated-credential create \
  --id <tesseraAppId> \
  --parameters '{
    "name": "k8s-tessera",
    "issuer": "https://<your-cluster-oidc-issuer>",
    "subject": "system:serviceaccount:default:tessera",
    "audiences": ["api://AzureADTokenExchange"]
  }'
```

## Security notes (full-security posture)

- **MFA required** (Conditional Access if licensed; otherwise security defaults).
- **No long-lived client secret** for the broker — the federated credential
  replaces it.
- **Least-privilege** Key Vault RBAC for the Tessera service principal; per-tenant
  envelope-key use for decrypt.
- The **medical / high-isolation** user should be a **member or B2B guest in the
  workforce tenant** (not a personal MSA) for clean delegation + role semantics
  ([ADR 0004](../../../docs/adr/0004-tenancy-and-isolation.md)).

## Cost

**$0** for a household: the Entra tenant is included free with the Azure
subscription; OIDC sign-in + on-behalf-of are Free-tier; Entra External ID is free
to 50,000 monthly active users. Conditional Access / Identity Protection (P1/P2)
are paid and **not** required here.
