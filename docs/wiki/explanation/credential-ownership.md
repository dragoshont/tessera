# Credential ownership

Every stored credential has an **owner**. Ownership is an axis separate from *who is
acting* and *what action* they take. It answers one question: **whose secret is
this?**

> The decision in full: [ADR 0020 — Credential ownership](../../adr/0020-credential-ownership.md).

---

## The three owners

| Owner | Whose secret | Example | May the owner ever be told it? |
|---|---|---|---|
| **`service`** (default) | Nobody personally — a shared household/team key. | A media-stack API key; a Home Assistant token. | No. Never revealed to anyone, including the acting user. |
| **`user`** | One person's own login. | Their medical portal, Gmail, Apple account. | The owner already knows it; "never reveal" protects it from agents and from *other* users. |
| **`dependent`** | Someone in another person's care, seeded by a guardian. | A child's or relative's account a guardian set up. | The guardian who seeded it may act for the dependent. |

The default is **`service`**, because it is the fail-safe: a secret that belongs to
nobody is never revealed to anybody.

---

## What ownership changes — and what it never changes

Ownership changes only **who, if anyone, may ever be told the secret**, and the
shape of consent and onboarding.

What ownership **never** changes — these hold for all three owners:

- The raw secret never reaches an agent.
- Every use is policy-gated and audited.
- One person's credential is never visible to another person.

So ownership is not a privilege escalation. A `user`-owned credential is not "more
powerful" — it simply belongs to a person, which shapes consent and the
(off-by-default) reveal path.

---

## Why it matters for the call path

When a request is authorised but the person has no personal key for a target, the
resolver may fall back to a **`service`-owned** shared key for that target. This is
how a household media key serves any granted member without anyone holding it. The
fallback applies **only** to a delegated request and **only** to a `service`-owned,
principal-less binding — never to a personal key, and never to a request with no
person.

The awareness dashboard surfaces the owner behind each delegation, so "who can act as
me" honestly shows when a shared service key is involved.

---

## Where to go next

- The full decision and consequences: [ADR 0020](../../adr/0020-credential-ownership.md).
- How owners appear in a binding: [Policy document reference](../reference/policy-document.md).
- The two identities that *use* a credential: [Identity model](identity-model.md).
