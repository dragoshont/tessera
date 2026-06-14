# ADR 0016 — Admin portal (headless-first convenience layer)

- **Status:** Proposed (2026-06-14)
- **Deciders:** maintainer (Dragoș)
- **Relates to:** [ADR 0008](0008-policy-and-identity-administration.md) (file-first
  administration), [ADR 0005](0005-identity-first-fail-closed.md) (verified identity),
  [ADR 0011](0011-identity-provider-sso.md) (Entra OIDC), [ADR 0004](0004-tenancy-and-isolation.md)
  (medical isolation), [ADR 0002](0002-broker-worker-topology.md) (harvest workers).
- **Detailed design:** [specs/admin-portal.md](../specs/admin-portal.md).

## Context

Tessera is operable headlessly today — file-first GitOps for policy, a CLI for the
rest ([ADR 0008](0008-policy-and-identity-administration.md)). That is correct for
the *operator*. It is painful for the **two human moments that cannot be scripted
away**:

1. **Seeding a session (the captcha hand-off).** A person's logged-in browser
   session is the credential for an un-API'd provider. Obtaining or renewing it
   requires a human to log in **once** in a remote browser and solve a reCAPTCHA
   the automation cannot. Today that means an SSH tunnel to a raw **noVNC** URL —
   an operator-only, error-prone, untrustworthy experience for what should be a
   2-minute "log in and solve the checkbox" task. There is **no rate-limit budget
   for fumbling** (real providers cap logins/day).
2. **Knowing what is connected.** "Which of my accounts are live? Which expire
   soon? Which need re-seeding?" has **no answer surface** — it lives in `kubectl`,
   vault blobs, and YAML. A household member cannot self-serve.

Adding a person/provider is likewise YAML-only: a `grant`, a `binding`, a seed.
The grant/binding are one-liners; the **seed is the hard part**, and it is exactly
the part files were never meant to hold (secrets live in the store, not in git).

The question the maintainer raised: *recipes can stay in files, but should adding
**accounts** use a UI?* And if we build a UI — **how do users authenticate, and
how do we keep it from becoming the place a security decision secretly lives?**

## Decision

**Build a small, OIDC-authenticated admin portal as a thin convenience layer over
the existing model — never a new source of truth.** It exposes three surfaces, in
ascending build order of value-per-effort:

1. **Live hand-off** — an in-portal embedded remote-browser stage that replaces the
   SSH+noVNC captcha solve. *The crown jewel; it relieves the only genuinely
   unavoidable human pain.*
2. **Connect-account wizard** — a resumable next-next-finish flow:
   `pick provider → name the person → seed the session (launches #1) → verify → done`.
3. **Accounts inventory** — a per-person, read-mostly dashboard of connections with
   health/expiry and per-account actions (re-seed, revoke). **Status and metadata
   only; secret values have no reveal affordance at all.**

The decision rests on a clean three-layer split that resolves the
*files-vs-UI* tension (the maintainer's explicit question):

| Layer | What it is | Where it lives | Does the portal author it? |
|---|---|---|---|
| **Recipes** | Provider *knowledge* — how a site works, its tools, selectors. Generic, shareable, committable. | **Files** (source of truth). | **No** — the portal *reads* recipes (the provider picker). Authoring a recipe stays a dev task (new site = config ± selector capture). |
| **Grants + bindings** | *Policy* — who may act as whom; which stored secret backs it. | **Files remain the source of truth** ([ADR 0008](0008-policy-and-identity-administration.md)). | **Proposes** — the portal compiles "connect Alice to Health Portal" into a grant+binding **change**, applied as a reviewable GitOps diff or via an admin API that writes the same file (hot-reload + audit). The reviewable-diff invariant is preserved. |
| **Sessions (the seed)** | The credential *bundle* in the vault, and the human act of obtaining it. | **The store** — never files (secrets are never in git). | **Owns** — this is the data plane; it requires the human captcha step; it is where a UI is *essential*, not optional. |

So: **recipes stay in files; an "account" gets a UI — precisely because an account
is ~90 % data-plane (a seeded session) and ~10 % policy (a one-line binding).**

**Authentication reuses the identity plane Tessera already requires** — Microsoft
Entra OIDC via `/common` ([ADR 0011](0011-identity-provider-sso.md)). The portal is
an OIDC confidential client; **no new auth system, no local admin password.** The
signed-in `oid`/`preferred_username` **is the same verified principal** that grants
and bindings key on — so "who am I in the portal" and "who Tessera brokers for" are
one identity. Two surfaces: **My accounts** (a person sees only their own) and
**Admin / All connections** (the operator, extra-audited, stricter gates for
medical), separated as distinct routes, not a role flag on one view.

**The headless-first rule is absolute** ([ADR 0008](0008-policy-and-identity-administration.md)):
the portal calls the broker's existing authorization; **default-deny still
decides**; the portal never holds a grant decision, never returns a secret value,
and everything it changes remains a reviewable file. The portal is a *window onto*
the broker, not a second brain.

## Consequences

- **Positive — the unavoidable human step becomes humane.** The captcha solve moves
  from "SSH + raw noVNC" to a trustworthy, time-boxed, anti-phishing-framed in-portal
  stage with server-detected success. This is the single biggest operability win.
- **Positive — self-service inventory.** A household member can see their own
  connections' health and re-seed an expiring one without the operator.
- **Positive — no model change, no new trust root.** Files stay the source of truth;
  auth reuses Entra; authorization stays in the broker. The portal slots into the
  seams ADR 0008 already designed ("an admin UI, if built, is a thin layer over the
  same file-backed model").
- **Positive — symmetry.** The portal user *is* the brokered principal; one identity
  spans sign-in, policy, and delegation.
- **Negative — new attack surface.** A web UI for a secrets-adjacent tool is a target.
  Mitigated by: OIDC-only sign-in, **no secret-value reveal anywhere**, short-TTL
  single-use live-view handles, step-up re-auth for destructive/medical/cross-person
  actions, the live browser staying in the **worker trust zone** (the cookie never
  crosses into the broker, per [ADR 0002](0002-broker-worker-topology.md)), and the
  portal living behind the same default-deny network posture.
- **Negative — a backend seam to build.** The portal needs a first-class
  **`connection`** read/act resource (status, health, presence-flags, audit) over
  today's vault blob + YAML, plus a **live-view handle** endpoint and a
  **draft-connection / job-queue** model. These are additive read-models and
  controllers, not changes to the security core. Detailed in
  [specs/admin-portal.md](../specs/admin-portal.md).
- **Negative — UI maintenance.** A frontend to keep. Mitigated by mirroring the
  workspace's established admin-UI stack (React + shadcn/ui + Tailwind + Storybook +
  Playwright) rather than inventing one.

## Rejected alternatives

- **Keep it CLI/noVNC-only.** Rejected: it leaves the one unavoidable human step
  (captcha) as the worst experience in the system, and offers no self-service
  inventory. "Operable headlessly" is true for the operator, not for a household.
- **Make the UI the source of truth (clickable policy store).** Rejected by
  [ADR 0008](0008-policy-and-identity-administration.md): a database/admin-UI as the
  *only* truth hides security changes and complicates audit/backup. Files stay the
  floor; the UI is a convenience over the API.
- **Author recipes in the UI.** Rejected for now: a recipe is provider knowledge
  (often needs selector capture / a dev's eye) and is generic/committable — it
  belongs in files and PR review, not a household member's wizard. The wizard
  *consumes* recipes; it does not write them.
- **Roll a local username/password login for the portal.** Rejected: a security
  tool that ships its own auth is a red flag; reuse the Entra OIDC plane Tessera
  already stands up.
- **Embed the captcha solve in a third-party hosted redirect (Plaid/Stripe-style).**
  Rejected: Tessera's whole promise is that *it* holds the session — the live view
  must be **self-hosted and in-portal** (Tessera already runs noVNC; Job A needs no
  new SaaS).
