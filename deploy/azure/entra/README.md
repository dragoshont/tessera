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
- The cluster's **OIDC issuer URL** (for workload identity federation).

## Deploy

```bash
az deployment sub create \
  --location westeurope \
  --template-file main.bicep \
  --parameters chatDomain=chat.example.com \
               clusterOidcIssuer="https://<your-cluster-oidc-issuer>"
```

Then wire LibreChat from the outputs:

```bash
OPENID_REUSE_TOKENS=true
OPENID_ISSUER=https://login.microsoftonline.com/<tenant-id>/v2.0
OPENID_CLIENT_ID=<chatAppId output>
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
