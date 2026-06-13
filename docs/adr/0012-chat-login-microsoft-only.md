# ADR 0012 — Chat login is Microsoft-only (Google can't reach the broker)

- **Status:** Accepted (2026-06-13)
- **Deciders:** maintainer (Dragoș)
- **Relates to:** [ADR 0009](0009-end-user-identity-propagation.md) (end-user
  delegation), [ADR 0011](0011-identity-provider-sso.md) (IdP = Microsoft Entra)

## Context

The chat briefly enabled **both** "Sign in with Google" and "Sign in with
Microsoft" during the unified-auth migration, on the assumption that keeping Google
was a harmless, zero-lockout fallback. It is not harmless. The question that
settled it: *can a Google login do what a Microsoft login does — forward a token
the broker accepts (the on-behalf-of / `OPENID_REUSE_TOKENS` path)?* The answer is
**no**, for three independent reasons.

## Decision

**The chat is Microsoft-Entra-only.** Google (and any other login that is not the
single issuer Tessera trusts) is **disabled** as a chat login provider.

### Why Google cannot substitute for Microsoft here

1. **Tessera trusts one issuer + audience.** The broker validates each forwarded
   token against `iss = https://login.microsoftonline.com/<tid>/v2.0` and
   `aud = <the Entra chat app>`. A Google token carries
   `iss = https://accounts.google.com` and a Google audience, so Tessera
   **rejects it** (fail-closed). A Google-logged-in user reaches the chat but every
   brokered tool call (the un-API'd providers, etc.) is denied.
2. **No token is even forwarded.** `OPENID_REUSE_TOKENS` only reuses the **OpenID
   strategy's** token. A Google-strategy session has no OpenID token to forward, so
   the delegated-identity assertion is simply absent — Tessera gets nothing.
   Cross-IdP **on-behalf-of does not exist**: OBO exchanges a token *within one
   issuer's* trust domain for a downstream scope of *that same issuer*; it cannot
   turn a Google token into an Entra-audience token.
3. **It splits identities.** The same person signing in with Google one day and
   Microsoft the next becomes **two distinct accounts** in the chat (different
   provider + identifier) — two separate memories, and per-user authorization
   (e.g. a medical MCP gate) keyed inconsistently. On a sensitive surface that is a
   correctness and privacy hazard, not a convenience.

So "keep Google as a fallback" actually ships a **second button that silently
yields a broken, tool-less, split-identity session.** A single trusted issuer is a
requirement of the delegation design ([ADR 0009](0009-end-user-identity-propagation.md)),
not a preference.

### What this means operationally

- The chat exposes **only** "Sign in with Microsoft" (`OPENID_*`); the Google
  provider env and config are removed, and the Google client credentials are
  retired from the secret store.
- Usernames are unchanged — a Microsoft account may be **backed by any email**
  (including a gmail address); retiring Google the *provider* does not change the
  *address* a person signs in with.
- If a non-Microsoft social login is ever wanted, it must come through a **single
  OIDC broker** (Authelia/Keycloak) that re-issues one token Tessera trusts — the
  deferred path already recorded in [ADR 0011](0011-identity-provider-sso.md), not
  a second native button.

## Consequences

- **Positive:** one trusted issuer end-to-end; delegation works for *every* signed-in
  user; no split identities; the medical per-user gate is unambiguous.
- **Negative:** the migration must be sequenced so the family is never without a
  working login — Microsoft is enabled and verified **before** Google is removed
  (the verification gate). Personal Microsoft accounts self-consent once on first
  sign-in.
- **Mitigation:** Microsoft login is enabled and its readiness verified (button
  live, discovery reachable, redirect URI matched) before Google is disabled, and
  the change is a single revertible commit.

## Rejected alternatives

- **Keep Google as a fallback login** — rejected: it can't forward a
  broker-acceptable token, has no cross-IdP OBO, and splits identities. A fallback
  that can't use the product's core feature isn't a fallback.
- **Make Tessera trust Google's issuer too** — rejected: multi-issuer trust on the
  credential broker widens the attack surface and still leaves the split-identity
  problem; the single-issuer rule is deliberate.
