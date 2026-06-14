# ADR 0020 — Credential ownership: user-owned vs service-owned vs dependent

- **Status:** Accepted (2026-06-14; implementation phased)
- **Deciders:** maintainer (Dragoș)
- **Relates to:** [ADR 0005](0005-identity-first-fail-closed.md) (verified
  identity), [ADR 0009](0009-end-user-identity-propagation.md) (per-call
  delegation), [ADR 0013](0013-per-user-access-tiers.md) (per-user tiers),
  [ADR 0014](0014-http-injectable-provider-egress.md) (credential-injected egress),
  [ADR 0015](0015-mcp-egress-through-tessera.md) (domain MCP egress),
  [ADR 0016](0016-admin-portal.md) (portal — connect wizard, live hand-off),
  [ADR 0017](0017-awareness-dashboard.md) (awareness dashboard),
  [ADR 0019](0019-app-integrations-and-user-delegated-actions.md) (actor split +
  read/use/manage planes).
- **Detailed design:** [service-access spec](../specs/service-access-adversarial-design.md#credential-ownership).

## Context

[ADR 0019](0019-app-integrations-and-user-delegated-actions.md) split requests by
**actor** (service-to-service stays direct; user/assistant/MCP/script/UI privileged
actions route through Tessera) and graded brokered actions by **plane**
(read/use/manage) and **risk** (step-up). A real question remains underneath both:

> **Who owns the upstream credential?** When Tessera holds a Regina Maria (RM)
> medical session, that is *my own* login — I configured it, I know the password,
> and my wife and son each own theirs. When Tessera holds a Seerr admin API key, no
> household member knows it or should; it is the deployment's key. "Never reveal the
> secret" means something different in each case.

The maintainer's framing is exact: *Tessera is a delegation agent*. For RM, Gmail,
and Apple, the **user already knows the password** — Tessera is not protecting the
secret *from its owner*; it is holding the live session so agents and other family
members do not need the secret, and so each person's account stays isolated. For a
shared service key, the secret is genuinely unknown to users and must never reach
*anyone*, including the acting user.

The model has implicitly conflated these. The presence of `onBehalfOf` already
encodes the difference (a delegated human vs pure automation), but the *consequences*
— seeding, revealing, revoking, onboarding, consent wording — have not been made
explicit. Doing so is what lets the upcoming RM (Mode U) and media-broker work be
modeled correctly rather than as one undifferentiated "credential."

## Decision

**Classify every connection by a `owner` axis with three values, orthogonal to the
actor/plane/risk axes, and let it drive seeding, reveal, revocation, onboarding, and
consent — while every ownership mode keeps the invariants that the secret never
reaches an agent, every action is policy-gated, and everything is audited.**

| Owner | Who owns the secret | Examples | The "full-head browser" | "Never reveal" protects against |
|---|---|---|---|---|
| **user** | the signed-in person (they configured it; they know it) | RM, Gmail, Apple iCloud | the **user's own** login session | leaking to **agents / other users** (the owner already knows it) |
| **service** | the deployment/operator only; users never know it | Seerr/Sonarr/Radarr/qBittorrent admin keys, shared SMTP, a shared Plex token | usually none (an API key); any session is the **operator's** | leaking to **anyone, including the acting user** |
| **dependent** | a person who cannot self-seed; a **guardian** seeds on their behalf | a child's RM/health/school account | the **guardian's** hands seed a session that **belongs to the dependent** | leaking to agents/others; and mis-attributing the data's true owner |

**The axis maps onto the existing model:**

- `owner: user` ⇒ a binding with `onBehalfOf = <that person>`; the person is the
  delegator ([ADR 0009](0009-end-user-identity-propagation.md), OAuth
  authorization-code shape). One binding per person (Dragoș, wife, son each own
  their RM).
- `owner: service` ⇒ a binding with `onBehalfOf = null` (pure automation /
  client-credentials shape). Authorization to *use* it is a separate grant naming
  the allowed callers/users.
- `owner: dependent` ⇒ a binding `onBehalfOf = <dependent>` **plus** a guardian
  relationship recording who may seed/act-as on their behalf (the RM MCP already
  has `act_as_dependent`). Owner-of-seeding ≠ owner-of-data; both are recorded.

**What each ownership mode treats differently:**

| Concern | user | service | dependent |
|---|---|---|---|
| **Seeding** (the session/captcha) | the **user** seeds their own (Job A live hand-off, [ADR 0016](0016-admin-portal.md)) | the **operator** configures the key once; no user captcha | the **guardian** seeds for the dependent |
| **Reveal** of a human-typed field | owner-only, behind step-up, audited, auto-redacted (it is *their* secret) — still never to an agent | **never, to anyone**, including the acting user | guardian-only if at all; default never |
| **Revocation** | the **user** may disconnect their own account | **operator-only** | guardian or operator |
| **Onboarding** | connect-wizard self-service ([ADR 0016](0016-admin-portal.md)) | operator config / GitOps ([ADR 0008](0008-policy-and-identity-administration.md)) | guardian-driven wizard with dependent selection |
| **Consent receipt** | "you connected your *own* X" | "you may *use* the household's X; you do not hold its key" | "you (guardian) connected X *for* <dependent>" |
| **Awareness dashboard** ([ADR 0017](0017-awareness-dashboard.md)) | shows under the owner's self view | shown as a household/service module, no personal owner | shows under the dependent, with the guardian noted |

**What every ownership mode keeps identical (the invariants):**

1. The secret **never reaches an agent/MCP/tool** — it is injected into egress or a
   worker, never returned ([ADR 0014](0014-http-injectable-provider-egress.md)).
2. Every action is **policy-gated** (actor + plane + risk, [ADR 0019](0019-app-integrations-and-user-delegated-actions.md)) and **default-deny**.
3. Every action is **audited** secret-free ([ADR 0017](0017-awareness-dashboard.md)).
4. Cross-user isolation holds: one user's user-owned connection is invisible and
   uncallable by another ([ADR 0013](0013-per-user-access-tiers.md)).

**Default ownership when unspecified is `service`** (the most restrictive reveal
posture: never reveal to anyone). A connection becomes `user`/`dependent` only by an
explicit, reviewed declaration — so a mislabeled connection fails *safe* (treated as
an unrevealable shared secret), never *open*.

## Consequences

**Positive**

- **Names the maintainer's real distinction.** RM/Gmail/Apple are user-owned
  delegations (Tessera holds *your* session so agents needn't, and keeps family
  members isolated); Seerr/Sonarr keys are service-owned authority (you wield the
  action, never hold the key). The model stops conflating them.
- **Makes Mode U and the media broker modelable.** RM → `owner: user`, one binding
  per person, user-seeded; the media keys → `owner: service`, operator-configured.
  Each gets the right seeding, reveal, and revocation behaviour by construction.
- **Encodes the family edge.** A child's account is `dependent`: the guardian seeds
  and may act-as, but the data belongs to the child — owner-of-seeding ≠
  owner-of-data is explicit, not folded into "self."
- **Fails safe.** Unspecified ⇒ service ⇒ never-reveal; only an explicit declaration
  loosens it, and only to the owner, behind step-up, audited.

**Negative / cost**

- **One more axis to author.** A binding now carries an `owner` (defaulted), and
  dependent connections carry a guardian relationship. Mitigated by the safe default
  and the connect-wizard capturing it.
- **Reveal is a genuinely new (small) surface** for user-owned secrets. It stays
  off by default; if ever built it is owner-only + step-up + auto-redact + audit,
  never bulk, never copy, never to an agent ([ADR 0016](0016-admin-portal.md) spec
  §5 already states the never-reveal default; this ADR scopes the *only* exception).
- **Dependent/guardian is a relationship model** beyond a flat allow-list. Kept
  minimal: a guardian may seed + act-as a named dependent; nothing more in v1.

## Rejected alternatives

- **One undifferentiated "credential."** Rejected: it forces a single reveal/seed/
  revoke policy, which is wrong for at least two of the three real cases and blocks
  correct modeling of RM vs media keys.
- **Infer ownership from the provider type** (e.g. "medical ⇒ user"). Rejected:
  ownership is a deployment fact, not a provider fact — the same provider can be
  user-owned (my RM) or dependent (my child's RM). Declare it; don't guess.
- **Make "user-owned ⇒ user may always reveal the secret."** Rejected: even the
  owner gets reveal only behind step-up + audit + auto-redact, and an **agent never**
  — the delegation promise ("agents act without the secret") must hold regardless of
  who the human owner is.
- **Default ownership to `user`.** Rejected as fail-open: an unlabeled shared key
  would inherit a reveal-to-owner path. Default to `service` so mislabeling fails
  closed.
