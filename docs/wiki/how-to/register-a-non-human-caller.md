# Register a non-human caller

This task gives a workload (a CLI, an MCP, an n8n flow, a CI job) **its own identity**,
so it can authenticate to Tessera as *itself* — with no long-lived secret stored in the
workload.

> The full steps, with the Bicep templates and the exact audience setup, are in the
> README:
>
> **[Registering a non-human caller (README)](../../../README.md#registering-a-non-human-caller-cli--automation--job)**

This page is a short map.

---

## The idea

A chat forwards a **person's** token. A workload has no person — it must prove **its
own** identity with **its own** token, and that token must have an audience Tessera
validates.

The safe default is **workload-identity federation**: the workload presents a token its
own platform already mints (GitHub Actions, a Kubernetes ServiceAccount, another Azure
workload) and exchanges it for a short-lived token. There is **nothing long-lived to
leak** — the top non-human-identity risk, removed.

## The steps (summary)

1. **One app registration per workload** — clean attribution, narrow blast radius.
2. **A federated credential, not a secret** — prefer a `federatedIdentityCredential`
   over a stored client secret.
3. **Exactly one app role** — no extra permissions.
4. **The right audience** — the workload requests a token for the shared system app, so
   the token's `aud` is the value Tessera validates, and it carries the workload's
   `appid`.
5. **A narrow grant in Tessera** — scoped to that `appid`, with no `onBehalfOf` (no
   person is involved):

   ```toml
   [[grant]]
   caller  = "<callerAppId>"   # the appid Tessera sees on the token
   target  = "marketplace"
   actions = ["read:listings", "read:prices"]   # least privilege; no writes
   ```

## How Tessera sees it

The token has no user (`oid` / `preferred_username` absent), so Tessera sees an
**app-only** caller and maps the `appid` to a verified caller identity
(`VerificationMethod.OidcJwt`). That `appid` is the **WHO** the grant names.

## If federation is not available

Fall back to a **client secret** only as a last resort (it is long-lived): store it in a
vault, scope it to the single role, keep its lifetime short, and rotate it. Prefer a
**certificate credential** over a plain secret, and a **federated credential** over both.

---

## Where to go next

- Now make the call: [Connect a domain MCP](connect-a-domain-mcp.md).
- The two identities involved: [Identity model](../explanation/identity-model.md).
- The grant fields: [Policy document reference](../reference/policy-document.md).
