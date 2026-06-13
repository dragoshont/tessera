# ADR 0011 — Identity provider & SSO: Microsoft Entra (no broker; Google off; GitHub deferred)

- **Status:** Accepted (2026-06-13)
- **Deciders:** maintainer (Dragoș)
- **Resolves:** the open IdP question from [ADR 0009](0009-end-user-identity-propagation.md)
  / [ADR 0010](0010-chat-client.md)
- **Relates to:** [ADR 0005](0005-identity-first-fail-closed.md) (verified identity),
  [ADR 0003](0003-credential-store-pluggable.md) (secretless store via WIF)

## Context

Per-user delegation ([ADR 0009](0009-end-user-identity-propagation.md)) needs each
user to present a **cryptographically verifiable signed token** (OIDC) that
LibreChat can reuse (`OPENID_REUSE_TOKENS`) and forward to Tessera as the
`subject_token`. So the identity provider must be an **OIDC** provider, and the
maintainer wants:

- **Microsoft as the primary login**, **Google disabled**.
- **GitHub as an optional alternative — not mandatory.**
- To support a person who **never used Azure but already has a personal Microsoft
  account** (live.com / outlook.com).
- **Full security**, with the Entra setup expressed as **Infrastructure-as-Code
  (Bicep)**.
- Ideally **no new tool** beyond what Azure already provides.

### Research findings (Microsoft Learn, 2026-06)

1. **An Entra tenant already exists and is free.** Every Azure subscription comes
   with a free Microsoft Entra ID tenant (the same directory the Key Vault service
   principal lives in). OIDC sign-in, app registrations, the on-behalf-of flow, and
   token issuance are **Free-tier** capabilities → **$0** for a household
   (Conditional Access / Identity Protection are the paid P1/P2 features, not
   needed). Even Entra External ID is **free to 50,000 monthly active users**.
2. **GitHub cannot be federated by Entra directly.** GitHub user sign-in is
   **OAuth 2.0 only** — it exposes **no OIDC discovery document, no `id_token`, and
   no SAML** for user login (GitHub's OIDC is only for Actions workflow tokens).
   Entra External ID's built-in social IdPs are **Google / Facebook / Apple**; its
   "custom IdP" slot requires **OIDC** (a discovery endpoint) or **SAML**. GitHub is
   neither. → To make GitHub work you must place an OAuth2→OIDC **translator**
   (a broker such as Authelia/Keycloak) in front — which *is* the extra tool.
3. **Personal Microsoft accounts work without Azure.** An app registered with
   `signInAudience = AzureADandPersonalMicrosoftAccount` lets a user sign in with an
   existing **personal** Microsoft account (live/outlook/Xbox) — no Azure
   subscription, no tenant membership required. (Trade-off: `appRoles` aren't
   honored for consumer MSA users at runtime; for clean role/delegation semantics,
   adding household members as **members/guests in the workforce tenant** is the
   stronger option.)
4. **Entra-as-IaC exists.** The **Microsoft Graph Bicep extension** (`v1.0`, GA
   2025) manages Entra resources (applications, service principals, federated
   identity credentials, groups) as Bicep alongside Azure resources.

## Decision

**Use Microsoft Entra ID directly as the OIDC identity provider. No broker.**

- **Microsoft = primary (and only, for iteration 1) login.** **Google: disabled.**
- **GitHub: deferred, not mandatory.** Because Entra can't federate GitHub without
  a broker, GitHub is **out of scope for iteration 1**. If it's wanted later, that
  is the (and the *only*) reason to introduce a broker (Authelia/Keycloak) — a
  deliberate, separate decision, not a hidden requirement now.
- **Account model:** register the chat app as `AzureADandPersonalMicrosoftAccount`
  so existing **personal Microsoft accounts work with zero Azure onboarding**; for
  the **medical / high-isolation** user, prefer a **member/guest in the workforce
  tenant** for clean delegation + role semantics.
- **`OPENID_REUSE_TOKENS=true`** + the `api://…/.default` scope + (Azure)
  on-behalf-of, so LibreChat forwards each user's signed Entra token to the Tessera
  MCP as the `subject_token` ([ADR 0009](0009-end-user-identity-propagation.md)).
- **Express the Entra setup as Bicep** (Microsoft Graph Bicep extension): the chat
  OIDC app registration *and* the Tessera **federated identity credential** that
  replaces the Key Vault client secret ([ADR 0003](0003-credential-store-pluggable.md)).
  See [specs/identity-azure-setup.md](../specs/identity-azure-setup.md) and
  [`deploy/azure/entra/`](../../deploy/azure/entra/).
- **Security defaults:** require MFA, short token lifetimes, least-privilege API
  permissions, no long-lived client secret where a federated credential or
  certificate can be used instead.

### The honest answer to "can I just use Entra with GitHub, no new tool?"

**No.** GitHub (OAuth2-only) can't be an Entra IdP without a broker. So:

| Want | New tool? |
|---|---|
| **Microsoft only** (chosen) | **None** — Entra is already there, $0 |
| Microsoft **+ Google/Facebook/Apple** | None (built-in Entra socials) — but Google is disabled by choice |
| Microsoft **+ GitHub** | **Yes — a broker** (Authelia/Keycloak) to translate GitHub OAuth2 → OIDC |

Since GitHub isn't mandatory, we keep the **zero-new-tool** path: **Microsoft Entra
only.**

## Consequences

- **Positive:** no new infrastructure; $0; the verified-token delegation primitive
  is native; personal Microsoft accounts onboard with no Azure friction; the whole
  identity setup is reproducible IaC.
- **Negative:** no GitHub login in iteration 1; personal-MSA users have weaker
  role/OBO semantics than tenant members.
- **Mitigation:** put high-isolation users in the workforce tenant; revisit a
  broker only if/when GitHub (or arbitrary social) login becomes a real
  requirement — at which point it federates *everything* uniformly.

## Rejected alternatives

- **Broker now (Authelia/Keycloak)** — rejected for iteration 1: it's the extra
  tool the maintainer wants to avoid, and Microsoft-only meets the requirement.
  Recorded as the *future* path for GitHub/arbitrary social.
- **Google as primary** — rejected by maintainer preference (disable Google).
- **GitHub as a custom OIDC IdP in Entra** — **not possible**: GitHub user login is
  OAuth2-only (no OIDC/SAML).
