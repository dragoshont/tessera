# Vocabulary reference (the enums)

This page lists the small set of fixed values Tessera uses, with their exact meanings.
These are the building blocks the [policy document](policy-document.md) and the
[API](broker-api.md) are made of. For plain-language definitions, see the
[Glossary](glossary.md).

---

## Action planes

The plane a verb operates on. It is **derived from the verb's namespace** — the text
before the first `:`. So a verb classifies itself.

> Source: `src/Tessera.Core/Policy/ActionPlane.cs`.

| Plane | Verb prefix | Meaning |
|---|---|---|
| `read` | `read:` | **Observe** — read state without changing it. |
| `use` | `use:` | **Operate** within configured behaviour — the *data plane*. |
| `manage` | `manage:` | **Reshape** the integration itself — the *control plane*. |
| (unspecified) | anything else | A legacy verb (`write:`, `pay:`) or a verb with no namespace. Old grants keep working. |

Two rules follow from the plane:

- A `manage:` action is authorised **only** by a manage-scoped grant pattern. A broad
  `*` or a `use:*` grant never reaches `manage:`.
- A `manage:` action **defaults to step-up**, even when granted, unless
  `policy.manageRequiresStepUp` is false or the action is exempted on the grant.

---

## Injection kinds

How a stored credential is added to the upstream request.

> Source: `src/Tessera.Core/Recipes/Recipe.cs` (`InjectionKind`) and
> `src/Tessera.Providers/ProviderHeaders.cs`.

| Value (`injection`) | What is injected | For |
|---|---|---|
| `bearer` | `Authorization: Bearer <access_token>` | OAuth-style APIs. |
| `apikey` | `<header>: <access_token>` (default header `X-Api-Key`; set `injectionHeader` to change it) | API-key providers — the Servarr / Seerr / *arr class. |
| `cookies` | `Cookie: …` built from the bundle (or from `cookieMap`) | Session-cookie portals. |
| (omitted) | nothing | A status-only recipe (`egress: none`). |

If the bundle lacks the material an injection kind needs, the egress **refuses** the
call (it never sends an unauthenticated request).

---

## Result classes

How much of a person's content a result may contain. They graduate exposure so a
search cannot spill a whole mailbox.

> Source: `src/Tessera.Core/Results/ResultEnvelope.cs`.

| Value (`resultClass`) | Contains | Notes |
|---|---|---|
| `metadata` | IDs, timestamps, sender/owner, title, status, size, small snippets. | The default for list/search. Capped tighter than a body. |
| `preview` | A sanitised body excerpt. | — |
| `fullBody` | Full text or structured content. | **By handle only** — the tool must read a `{handle}` from a prior search. |
| `attachment` | Binary or bulk output. | **By handle only** — explicit export. |
| `receipt` | A mutation summary: before/after + object id + confirmation id. | What a write returns, instead of a fresh body. |

A **handle** is an opaque, single-provider reference returned by a search. It carries no
body. A handle from one provider cannot be replayed against another.

---

## Credential owners

Whose secret a stored credential is. See [credential ownership](../explanation/credential-ownership.md).

> Source: `src/Tessera.Core/Resolution/CredentialOwner.cs`.

| Value (`owner`) | Whose secret | Reveal |
|---|---|---|
| `service` (default) | Nobody personally — a shared household/team key. | Never revealed to anyone. |
| `user` | One person's own login. | The owner already knows it; hidden from agents and other users. |
| `dependent` | Someone in a guardian's care, seeded by them. | The guardian may act for the dependent. |

---

## Verification methods

How an identity was established. All except `Dev` are cryptographically verified.

> Source: `src/Tessera.Core/Identity/VerificationMethod.cs`.

| Value | Verified? | Meaning |
|---|---|---|
| `Dev` | **No** | Unverified. Local development only; the policy denies it off loopback. |
| `Mtls` | Yes | A mutual-TLS client certificate. |
| `SpiffeSvid` | Yes | A SPIFFE X.509-SVID. |
| `OidcJwt` | Yes | A validated signed OIDC / JWT assertion (signature, audience, expiry, issuer, tenant). |
| `Network` | Yes | Trusted by the network boundary: the chat→Tessera hop is NetworkPolicy-gated and a valid end-user token accompanies the call (the shared chat caller). |

---

## Egress modes

How the broker performs the upstream call for a target.

| Value (`egress`) | Meaning |
|---|---|
| `none` | No upstream egress. The broker only authorises and reports credential status (the safe default). |
| `http` | HTTP-injectable: the broker injects the credential and forwards to an allow-listed upstream. |

---

## Where to go next

- See these used in a real policy: [Policy document reference](policy-document.md).
- The plain-language versions: [Glossary](glossary.md).
- Why planes and result classes exist: [ADR 0019](../../adr/0019-app-integrations-and-user-delegated-actions.md).
