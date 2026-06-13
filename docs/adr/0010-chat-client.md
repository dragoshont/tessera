# ADR 0010 — Chat client: fork LibreChat (harden toward multitenant enterprise)

- **Status:** Accepted (2026-06-13)
- **Deciders:** maintainer (Dragoș)
- **Resolves:** the open "chat client" choice in
  [ADR 0009](0009-end-user-identity-propagation.md)
- **Relates to:** [ADR 0005](0005-identity-first-fail-closed.md),
  [ADR 0004](0004-tenancy-and-isolation.md)

## Context

The chat is the **human surface** of the whole product and is "extremely
important." Requirements:

- Keep the **LibreChat-style experience** (agents + picking up MCP servers).
- **Reuse as much existing code as possible** — forking is accepted as the reality.
- Position as a **multitenant, security-first, enterprise-ready** chat.
- An **admin panel**: user roles, adding users, and SSO (Google, Microsoft,
  GitHub).
- **Per-user MCP delegation** (the [ADR 0009](0009-end-user-identity-propagation.md)
  crux) and **WebRTC voice**.

Three candidate bases were evaluated: a **minimal in-house WebRTC voice
prototype** (a small `voice-gateway` the maintainer already built), **OpenWebUI**,
and **LibreChat**.

### What the maintainer already has

The local `dragoshont/LibreChat` fork (branch `feat/realtime-voice`) **already**
contains, on top of upstream LibreChat:

- **WebRTC realtime voice** end-to-end (`api/server/routes/realtime.js`,
  `client/.../VoiceChat.tsx`, `useRealtimeVoice.ts`, voice web-search tool, Azure
  schema sanitization).
- **Per-user MCP gating** (`MCP_USER_GATE`) — the delegation work is *already
  started here*.

So "build our own" or "grow the voice prototype into a chat" would **discard the
maintainer's own voice + MCP work** *and* LibreChat's entire feature set.

### Comparison

| Capability (for this product) | LibreChat | OpenWebUI | in-house voice prototype |
|---|---|---|---|
| Agents + **first-class MCP** (the valued experience) | **native, mature** | tools/functions + MCP via MCPO (less MCP-first) | none (build it all) |
| SSO: Google / Microsoft(Entra) / GitHub | **all, + OIDC/SAML/LDAP** | OAuth/OIDC/LDAP | none |
| Admin panel + RBAC | **admin panel; ACL; custom roles; groups; per-resource Viewer/Editor/Owner** | admin panel; roles; groups; model-access control | none |
| **Per-user MCP OAuth / delegation** | **per-user OAuth; `OPENID_REUSE_TOKENS`; `{{LIBRECHAT_OPENID_*}}` header forwarding; Azure on-behalf-of** | per-user keys; weaker MCP delegation story | none |
| WebRTC voice | **already in the fork** | not native | **is the prototype** (partial) |
| Stack | Node / React | Python / Svelte | Node (gateway) |
| License | **MIT (fork-friendly)** | BSD-3-ish | own |
| Reuses maintainer's existing code | **yes (the fork)** | no | partial |

The decisive technical finding: LibreChat's **`OPENID_REUSE_TOKENS=true`** makes
the user's refresh/access token *issued by the OIDC provider*, and YAML-defined MCP
servers can forward it via **`{{LIBRECHAT_OPENID_*}}`** headers (plus Azure
on-behalf-of). **That is precisely the verified per-user `subject_token` ADR 0009
requires** — Tessera can be wired as a YAML MCP server that receives each user's
own signed token per call. The primitive exists; the earlier "LibreChat can't
delegate" note reflected an older state of the fork.

## Decision

**Continue and harden the existing `dragoshont/LibreChat` fork as the Tessera chat
client.** Do **not** build a new chat or expand the in-house voice prototype into
one. Keep the LibreChat experience; fold the existing WebRTC voice in; integrate
Tessera as the per-user-delegated MCP broker; and close the enterprise/multitenant
gaps below.

### What we get "for free" (reuse, not rebuild)

SSO (Google/Microsoft/GitHub/OIDC/SAML/LDAP), the **admin panel + RBAC/ACL**
(roles, custom roles, groups, per-resource sharing, user add/remove scripts),
agents + first-class MCP, per-user MCP OAuth, and the maintainer's own WebRTC voice
+ `MCP_USER_GATE`.

### The hardening gaps to close in the fork (the real work)

1. **Per-user isolation, not org multi-tenancy** *(decided 2026-06-13).* The chat
   is **single-tenant**: one organization, one deployment. It is **not** multi-org,
   so we do **not** build a hard org-tenant boundary into LibreChat (this removes
   what was previously the single biggest workstream). The requirement instead is
   **complete isolation between end-users** on two axes:
   - **Memory** — each user's memory/context is private; no cross-user leakage.
     LibreChat memory is already per-user; the work is to **verify** that and to
     **lock down cross-user sharing** (agents/prompts/MCP `share`/`public` set off
     so one user's context can't reach another) for "completely isolated".
   - **Auth against Tessera** — each user authenticates to Tessera as *themselves*
     (own signed token → own Tessera tenant → own envelope key → own credential
     set, per [ADR 0004](0004-tenancy-and-isolation.md) where *Tessera's* tenant =
     the end-user). No user can reach another's credentials.
   So: **chat = single org tenant; Tessera = one tenant per user.** The
   high-sensitivity (medical) end-user can still map to the ADR 0004
   **dedicated-instance** Tessera tier. No full multi-tenant chat rewrite is needed.
2. **Per-user → Tessera delegation, verified.** Wire `OPENID_REUSE_TOKENS` +
   `{{LIBRECHAT_OPENID_*}}` so each call to the Tessera MCP carries the user's
   **signed** token; **validate Tessera can verify it** (sig/`aud`/`exp`/`jti`/
   issuer) as the `subject_token`. Generalize `MCP_USER_GATE` into this.
3. **Security review of MCP credential surface.** LibreChat's `customUserVars` and
   DB-sourced servers have intentional placeholder restrictions; audit that no
   path lets one user reach another's tokens, and that secrets are never logged.
4. **WebRTC voice → first-class consumer.** Promote the existing realtime voice to
   a supported, delegated consumer (the voice turn carries the same per-user
   identity to Tessera as text does).
5. **Enterprise polish.** Admin UX for tenant + role management, audit surfacing,
   step-up approval UX for high-impact actions, hardened defaults.

### Upstream discipline

Track `upstream` (danny-avila/LibreChat); keep our changes as a **reviewable
overlay** (feature-flagged where possible) so we can merge upstream improvements.
Contribute generically-useful pieces back where it makes sense.

## Consequences

- **Positive:** maximum reuse (LibreChat + the maintainer's own voice/MCP work);
  the valued experience is preserved; SSO + admin + RBAC already exist; the
  delegation primitive exists; MIT license is fork-friendly.
- **Positive:** because the chat is **single-tenant** (one org) with **per-user
  isolation**, we avoid a hard org-multi-tenant rewrite — the work is verifying
  per-user memory privacy, locking cross-user sharing, and per-user Tessera
  delegation, all of which build on LibreChat's existing per-user model.
- **Negative:** we inherit a large Node/React codebase and must maintain a fork and
  rebase on upstream.
- **Mitigation:** keep changes as a thin, feature-flagged overlay; map the
  high-sensitivity end-user to the dedicated-instance Tessera tier rather than
  hardening one shared store for everyone.

## Rejected alternatives

- **Build our own chat / grow the in-house voice prototype** — rejected: discards
  the maintainer's own voice + MCP work and LibreChat's whole feature set; years of
  surface (SSO, admin, RBAC, agents, MCP) to re-implement.
- **OpenWebUI** — rejected: capable and has RBAC/SSO, but its tool/agent model is
  less MCP-first, it doesn't carry the maintainer's existing voice work, and the
  per-user MCP **delegation** story is weaker than LibreChat's
  `OPENID_REUSE_TOKENS`.
