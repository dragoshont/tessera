# What Tessera is and why

This page explains the problem Tessera solves, the idea behind it, and — just as
important — what Tessera is **not**.

---

## The problem

AI agents and automations are useful only when they can *act on real accounts*. An
assistant that reads your medical results, a script that checks a price, a workflow
that adds a calendar event — each must **log in** to some service.

Today, people make this work the dangerous way: they paste a long-lived API key or a
password directly into the tool's configuration.

This is the single largest class of
[non-human-identity risk](https://owasp.org/www-project-non-human-identities-top-10/):

- The secret **leaks** (it sits in a config file, a log, an environment variable).
- The secret is **over-privileged** (it can do far more than the one task needs).
- The secret **never expires** (no one rotates it).
- The secret can be **stolen by a trick** (an agent given a malicious instruction
  can be made to hand it over or misuse it).

Hosted products exist (Arcade, Composio), but they are **software-as-a-service** and
they assume every target already speaks OAuth. The services real people care about —
a health portal, a regional marketplace, a utility account — often have **no OAuth
and no API**, only a human login.

---

## The idea

**Tessera is a trusted doorkeeper between the caller and the accounts.**

- The caller proves **who it is** (and, when acting for a person, **for whom**).
- Tessera checks **the rules**.
- If the rules allow it, **Tessera does the logging-in itself** and returns only the
  answer.

The secret stays **inside** Tessera. The caller holds nothing.

> **Before:** you give the robot your house key. It is scary — the key can leak, or a
> tricked robot can hand it to a stranger.
>
> **With Tessera:** the robot rings a doorkeeper who has the key. The doorkeeper opens
> the door for one specific task. The robot never touches the key.

If you take the "key" from a caller that has been tricked, you have taken **nothing**
— the caller never had a key, only a door that Tessera opened for it, under policy,
for one action.

---

## What makes Tessera different

| Property | What it means |
|---|---|
| **Open source, self-hosted** | You run it. No third party holds your secrets. |
| **Handles the un-API'd web** | It can broker services that have only a human login, not just OAuth APIs. |
| **Per-end-user** | It acts as a *specific person*, not as one shared account. |
| **Secretless transit** | It reaches its own secret store without holding a store password. |
| **Request-aware** | It understands *which* action is being asked, so it can allow a read but deny a delete with the same key. |

That last property is the heart of it. Tessera is **not** a pipe that blindly
forwards requests. It knows that `read:series` and `use:delete` are different, so it
can authorise each one separately. See [how a call works](how-a-call-works.md).

---

## What Tessera is **not**

Knowing the boundary is as important as knowing the purpose.

- **Not a token-passthrough proxy.** Tessera will not forward the caller's token to
  the upstream. That is a known anti-pattern; it breaks audit, audience binding, and
  least privilege. Tessera validates the caller's token for *itself* and injects a
  *separate* upstream credential.
- **Not a blind URL proxy.** A provider with no recipe is **not** routed through
  Tessera. Tessera only brokers calls it can *authorise*.
- **Not your first single sign-on (SSO).** Tessera validates a login token that an
  identity provider already issued. It is not an app catalogue, an MFA manager, or a
  browser-session manager. Those belong to an access gateway (Authentik, oauth2-proxy)
  — see [positioning](positioning.md).
- **Not a secret store.** Secrets rest in Key Vault or Vault. Tessera fetches them
  *just-in-time* and never persists them itself.
- **Not a runner of arbitrary shell.** Brokering "run any command" is an explicit
  non-goal. That credential class (an SSH key for a shell) stays with its own tool.

---

## Where to go next

- See the idea in motion: [How a call works](how-a-call-works.md).
- See who is involved: [Identity model](identity-model.md).
- Try it yourself: [Your first brokered call](../tutorials/01-your-first-brokered-call.md).
- See where it sits in a real stack: [Positioning](positioning.md).
