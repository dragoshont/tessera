# Spec — Admin portal

> Status: **Proposed / design-phase** ([ADR 0016](../adr/0016-admin-portal.md)).
> A thin, OIDC-authenticated convenience layer over the existing broker. It does
> **not** change the security model: files stay the source of truth
> ([ADR 0008](../adr/0008-policy-and-identity-administration.md)), the broker keeps
> deciding (default-deny), and **no secret value is ever shown**. The portal exists
> for the two human moments the CLI can't make humane: **seeding a session
> (captcha)** and **knowing what's connected**.

## 1. Why a portal (the two unavoidable human moments)

Everything else about Tessera is automatable. Two things are not:

- **The captcha solve** — only a human can pass a fresh reCAPTCHA, once, to seed a
  session. Today: SSH tunnel → raw noVNC. There is no retry budget (providers cap
  logins/day), so the experience must be *right the first time*.
- **Self-service awareness** — a person should see *their own* connections' health
  and re-seed an expiring one without the operator.

The portal targets exactly these. It is **not** a policy editor (files +
GitOps remain that) and **not** a recipe authoring tool (recipes are dev-authored
provider knowledge). See [ADR 0016](../adr/0016-admin-portal.md) §Decision for the
three-layer split.

## 2. Information architecture

```
Tessera portal  (OIDC sign-in — Microsoft /common, ADR 0011)
│
├── My accounts            ← default landing for everyone (self scope)
│     ├── [connection rows: provider · health · last re-seeded · expires · ⋯]
│     ├── Connect account ▸ (wizard)
│     └── connection detail drawer (metadata + presence flags + audit timeline)
│
├── Action required         ← inbox: "finish login for Alice → Health Portal"
│     └── Live hand-off stage (the captcha surface)
│
└── Admin · All connections ← operator only; step-up to enter; extra-audited
      ├── everyone's connections (owner column)
      ├── refresh / re-seed queue (bulk jobs, per-item progress)
      └── audit explorer (secret-free)
```

Two **separate surfaces**, not one view with a role flag: *My accounts* (a person
sees only their own) and *Admin / All connections* (the operator). Medical
connections inherit the dedicated-instance posture ([ADR 0004](../adr/0004-tenancy-and-isolation.md))
— stricter UI gates (per-action re-auth, optional dual-control).

## 3. Job A — Live hand-off (the captcha surface)

The highest-value surface. Replaces SSH+noVNC with an in-portal embedded
remote-browser stage. Pattern lineage: Browserbase Session Live View (embedded
iframe, read/write, `postMessage` on disconnect), Plaid Link (focused modal,
detect success, 30-min timeout, "update mode" = re-seed), Stripe hosted onboarding
(short-TTL single-use link, `refresh_url` on expiry, *return ≠ complete* → verify
server-side).

### 3.1 Flow

```mermaid
sequenceDiagram
    participant U as Person (browser)
    participant P as Portal
    participant B as Broker
    participant W as Harvest worker (noVNC/CDP)
    participant V as Vault

    U->>P: "Re-seed Health Portal" (or wizard step 3)
    P->>B: request live-view handle {person, target}
    B->>W: ensure warm slot on the person's profile; arm login URL
    B-->>P: { live_view_url (short-TTL, single-use), mode, ttl }
    P->>U: Live Stage — embedded remote browser + task list + countdown
    U->>W: logs in, solves the reCAPTCHA checkbox
    W-->>B: liveness signal (post-login URL / success cookie present)
    B->>V: harvester writes the session bundle
    B-->>P: status → Verifying → Live (server-detected; no click needed)
    P->>U: ✓ "Session live" (manual "I'm done" fallback remains)
```

### 3.2 The Live Stage (states + chrome)

```
┌───────────────────────────────────────────────────────────┐
│ 🔒 contulmeu.example.ro — verified · ⏱ 4:38 left · ⤢ Pop out │  target-identity strip
├──────────────────────────────────────────┬────────────────┤
│                                          │  What to do      │
│            [ live remote browser ]        │  ① Log in        │
│            (read/write, 16:10)            │  ② Solve checkbox │
│                                          │  ③ We take over ✓ │
│                                          │  ● Waiting for you │
├──────────────────────────────────────────┴────────────────┤
│ We keep the logged-in session. We never see your password.  │  persistent trust line
└───────────────────────────────────────────────────────────┘
```

- **Target-identity strip** — real favicon + **verified hostname** + lock. The
  anti-phishing anchor: the user must see they are on *their* provider, with
  Tessera chrome clearly *around* the browser, never *as* it.
- **Status pill** (state machine): `Connecting → Waiting for you → Verifying → Done`,
  plus `Reconnecting…` and `Expired`.
- **Countdown** tied to the handle TTL (show it; never surprise-expire).
- **Right rail = a 3-item task list**, one job per line — not prose.
- **Server-detected success auto-advances** (Tessera can see the cookie; Plaid
  can't) → flips to `Verifying → Done` with **no required click**; keep a manual
  *"I'm done"* fallback.
- **Pop-out** to a full tab (same session) for small screens / clipboard quirks.
- **Expiry is graceful** (Stripe `refresh_url`): expired handle → *"This window
  timed out. Start again"* re-arms a fresh session — never a blank noVNC error.

### 3.3 Async + out-of-band (captcha can take minutes)

- Treat the solve as an **async job with a live feed** (the live browser *is* the
  feed), not a blocking spinner.
- **Leave-and-resume**: walking away → *"Paused — resume within 8:00"* and a row in
  **Action required**.
- **Out-of-band nudge** (the biggest win over today's SSH flow): when an agent run
  blocks on "needs a human to seed *Alice → Health Portal*", fire email/push with a
  one-tap deep link back to the Live Stage.

### 3.4 Security (Job A)

- **Short-TTL, single-use, identity-bound** live-view handles — never a long-lived
  or shareable noVNC URL. Issued **only to the authenticated user inside the
  portal** (Stripe's "don't share this link"; link-preview bots burn it anyway).
- The live browser stays in the **harvest-worker trust zone** ([ADR 0002](../adr/0002-broker-worker-topology.md));
  the cookie is written to the vault by the worker — **it never transits the
  broker process**.
- Plain-words trust line, always visible: *"We keep the session. We never see your
  password."* (true to the model — Tessera only ever reads + reports status).
- **Anti-patterns:** raw noVNC URL with no chrome; infinite spinner; forcing a
  "done" click we could detect; cramming the live browser into a small modal;
  hosting a Tessera-branded page that mimics the target (phishing-shaped).

## 4. Job B — Connect-account wizard

`pick provider (recipe) → name the person → seed session (→ Job A) → verify → done`.
Pattern lineage: WorkOS Admin Portal (provider picker → guided steps → *Test
connection*), Vercel new-project (async step streams real progress), GitHub App
install (explicit capability-review before commit), Stripe activate-checklist
(resumable, "1 step left").

| Step | Screen | Notes |
|---|---|---|
| 0 | **Empty state** — "Connect your first account", one primary CTA | Tailscale/Vercel first-run. |
| 1 | **Pick provider** — grid of **recipe tiles** (favicon + name) + search; "Don't see it? Request a recipe." | Reads `recipes[]`; does not author them. |
| 2 | **Name the person** — "Who is this account for?" pick/create a **person** | The delegated `onBehalfOf` principal, in plain language (not YAML). |
| 3 | **Seed session** — launches **Job A** inline or "Open secure window" | The async hard step. A **draft connection** persists server-side (Stripe: the account object exists *before* onboarding completes); closeable + resumable. |
| 4 | **Verify** — broker runs its **read-only self-test** → green "Session live" | A *real* probe (Tessera already self-tests per [getting-started.md](../getting-started.md)); never claim "connected" without it. |
| 5 | **Done** — capability review: "As **Alice**, the agent may *read appointments* · may **not** *pay or book*" + deep-link to the new inventory row | GitHub-install review screen; echoes default-deny grants. |

- **Resumability**: closing mid-wizard leaves a **"Resume — 1 step left"** card
  (a draft with `resumable_until`); the async seed can finish out of band.
- **What the wizard writes**: on completion it proposes the **binding** (and grant,
  if new) as a reviewable file change (GitOps diff or admin-API write + hot-reload +
  audit, per [ADR 0008](../adr/0008-policy-and-identity-administration.md)); the
  **session bundle** lands in the vault only after a successful seed.
- **Anti-patterns:** losing state mid-captcha; declaring success without a verify
  probe; dumping grants/bindings/recipes YAML on a household user; fake progress bar
  during the seed; a 10-field single screen (one decision per step).

## 5. Job C — Accounts inventory ("check all added passwords")

Per-person, read-mostly. Default landing = *"Alice's accounts."* Pattern lineage:
GitHub Actions / Vercel secrets (**write-only — value never re-shown**), 1Password
Watchtower (health badges, concealed fields), Vault (metadata-rich, value-stingy),
Fleet (per-subject inventory + bulk-action queue), Tailscale (proactive expiry).

```
Alice's accounts                                   [ + Connect account ]
─────────────────────────────────────────────────────────────────────
PROVIDER            HEALTH          LAST RE-SEEDED   EXPIRES
🏥 Health Portal    ● Live          12 days ago      ~estimated   ⋯
🏦 Utility Co       ◐ Expiring 2d   88 days ago      in 2 days    ⋯   (amber)
🛒 Marketplace      ○ Absent        —                —            ⋯   (needs seed)
📨 Webmail          ⚠ Error         3 days ago       —            ⋯
─────────────────────────────────────────────────────────────────────
2 need attention            [ Re-seed all expiring (2) ]
```

- **Rows = connections**, not secrets: provider, the **person it acts as**, a
  **4-state health badge** (`Live · Expiring soon · Absent · Error` — calm colors,
  red only for true error), **last re-seeded** (relative), **expires** (honest
  *"~estimated / unknown"* — cookies often have no readable TTL), and a `⋯` menu
  (**Re-seed** → Job A, **Revoke**, **View activity**).
- **The never-reveal stance (core security pattern):** the password/cookie value has
  **no reveal affordance at all.** The UI states it: *"Tessera can't show this —
  that's the point."* Stronger and more honest than reveal-behind-reauth. *(If a
  future need forces surfacing one human-typed field, only then: step-up re-auth →
  reveal one field → auto-redact after N seconds → audit event. Never bulk-reveal,
  never copy.)*
- **Detail drawer** (click a row): metadata only — **bundle-field presence**
  (`has cookies ✓ · has refresh token ✓` — presence, not value, per
  [CredentialBundle.cs](../../src/Tessera.Core/Stores/CredentialBundle.cs)), the
  recipe, owner, the **secret-free audit timeline** (Tessera audit is already
  secret-free), and the **policy** backing it ("who/what may use this").
- **Bulk queue**: *"Re-seed all expiring"* → a job list with per-item
  `Queued → Needs human → Live` (each re-seed may trigger a captcha → Job A). This
  is the refresh-queue, surfaced.
- **Proactive expiry**: email/push **before** a session expires; a dashboard banner
  *"2 accounts expiring this week."* Show **"last successfully used"** alongside
  "expires" for trust.

## 6. Authentication & RBAC

- **OIDC/SSO-first — reuse the plane Tessera already requires.** The portal is an
  Entra OIDC confidential client on the same `/common` issuer + chat/system app id
  ([ADR 0011](../adr/0011-identity-provider-sso.md), [deploy/azure/entra](../../deploy/azure/entra/)).
  **No local admin password.** The signed-in `oid`/`preferred_username` **is** the
  principal that grants/bindings key on — sign-in identity == brokered identity.
- **Two surfaces** (separate routes): **My accounts** (`viewer_scope=self`) and
  **Admin / All connections** (`viewer_scope=admin`, entry gated by step-up,
  every action extra-audited).
- **Step-up / re-auth** (sudo-mode window) for sensitive actions:

  | Action | Gate |
  |---|---|
  | Enter Admin / All connections | step-up re-auth (short-lived sudo window) |
  | Revoke / delete a connection | **type-the-name-to-confirm** + (medical) per-action re-auth |
  | Re-seed a **medical** connection | per-action re-auth ([ADR 0004](../adr/0004-tenancy-and-isolation.md)) |
  | View another person's connection (admin) | step-up + extra audit |

- **Anti-patterns:** a local admin password as primary auth; one screen showing
  everyone's accounts to every signed-in user; no re-auth before revoke/medical;
  persistent god-mode with no audit.

## 7. Security model (summary)

The portal adds **zero** new trust roots. It inherits every invariant and adds UI-
specific guards:

1. **Headless-first** — the portal calls the broker's authorization; **default-deny
   decides**; the portal never holds a grant decision and everything it changes
   stays a reviewable file ([ADR 0008](../adr/0008-policy-and-identity-administration.md)).
2. **No secret egress** — no reveal, no copy, no value in any response or log. The
   broker already returns status only; the portal preserves that.
3. **Short-TTL, single-use, identity-bound live-view handles**; the live browser
   stays in the worker trust zone (cookie never crosses the broker).
4. **Step-up for destructive/medical/cross-person**; type-to-confirm for
   irreversible; admin surface extra-audited.
5. **Same network posture** — the portal sits behind default-deny ingress like the
   broker; OIDC-gated; medical → dedicated stamp.
6. **Secret-free audit of every sensitive action** `{actor (human|agent), action,
   decision, target, person, timestamp, jti}` — Tessera already has the spine.

## 7a. Known risks & open questions (self-review)

Honest gaps a skeptical reviewer should see before build. None reshapes the model;
each is a sequencing or design decision to pin in its phase.

| # | Risk | Why it matters | Resolution |
|---|---|---|---|
| **R1** | **Live-view handle depends on the worker `act()` channel, which the roadmap defers** ([roadmap.md](../roadmap.md): browser/Android `act()` egress + gRPC workers are deferred). | Job A (the crown jewel) needs a worker that serves a live remote browser and writes the bundle without the cookie crossing the broker. Today the homelab does this with a **standalone noVNC + harvester** (`sessionkeeper`), not a Tessera worker. | **Phase 1 wraps the existing noVNC/harvester** as the first browser worker (the broker brokers a handle to it), rather than waiting on the full gRPC `act()` channel. The worker-trust-zone invariant holds because the cookie still only goes worker→vault. |
| **R2** | **"Server-detected success auto-advances" assumes a reliable per-provider liveness signal.** | For a generic recipe, "logged-in" detection (post-login URL / success cookie) is provider-specific; a generic detector may be unreliable, making the **manual "I'm done"** the common path, not the fallback. | Treat auto-advance as **best-effort per recipe** (the recipe already declares a `success_when` cookie); always ship the manual confirm. Don't gate the flow on auto-detect. |
| **R3** | **Wizard binding write-back reintroduces the "UI mutates policy" tension** ADR 0008 warns about. | If the wizard hot-writes a grant/binding, that is a security change not landing as a reviewed diff. | **Default to a GitOps PR** (the wizard *proposes* a diff a human merges) for grants/bindings; offer a hot admin-API write **only** behind step-up + its own audit event, never silent. Pin this per deployment before Phase 2. |

## 7b. Do we need a database? (decision: not initially)

**No database for the first phases.** The portal's data is a **projection over what
already exists**, plus two small mutable sets that start in-memory/file-backed:

| Portal concept | Source of truth | Needs a DB? |
|---|---|---|
| **Users / people** (e.g. the household members) | **OIDC principals** (Entra) + the `onBehalfOf` values already in `grants`/`bindings` + a small **admins** allow-list in config | **No** — derived. |
| **Who is admin** | a config list (`portal.admins: [ ... ]`) — same principal the broker verifies | **No** — config. |
| **Connections + health** | a **computed projection** over `recipes`/`grants`/`bindings` (files) + **vault status** (presence/last-rotated) | **No** — projection; cache in memory, refresh on read. |
| **Draft connections** (wizard in-progress) | short-lived; survives a reload | **No** — in-memory with a TTL first; **file-backed** if drafts must survive a pod restart. |
| **Job queue** (bulk re-seed) | transient work items | **No** — in-memory first; file/queue later if it must survive restarts. |
| **Audit** | the broker's existing **append-only secret-free audit** | **No** — already exists; the portal reads it. |

So the "users" an operator sees (**e.g. alice = admin; bob + carol =
members**) are **not rows in a database** — they are the verified OIDC principals
that already appear in the policy files, classified by the admins allow-list. A DB
becomes worth it only at the scale ADR 0008 already names (thousands of tenants /
self-service editing) — and the loader seam absorbs it then **without changing the
model**. **Start DB-less; add persistence per-concept only when a restart-survival
or scale need is proven.**

## 8. Backend seam the portal needs

Today: a vault **blob** + YAML grants/bindings/recipes. The portal needs a
first-class **`connection`** resource to list and act on. **None of this leaks
secret values** — additive read-models + controllers over the security core, not a
change to it.

**`connection`** (Job C list + Job B result):
- `connection_id`, `owner_person_id` (acts *as*), `recipe_id`/provider, `display_name`
- `target`: canonical hostname, favicon URL, login URL (Job-A identity strip)
- `status`: `live | expiring_soon | absent | error | seeding | needs_human`
- health: `last_verified_at`, `last_used_at`, `last_seeded_at`, `seed_method`,
  `expires_at` **+ `expiry_is_estimated: bool`**
- **bundle presence flags only**: `has_cookies`, `has_refresh_token`, `has_access_token`
- policy linkage: which `grants`/`bindings` back it

**`live_view_handle`** (Job A): `live_view_url` (short-TTL, single-use),
`mode: readonly|readwrite`, `session_ttl_seconds`, `expires_at`, a server
`liveness_signal`, and a `disconnected`/`done` event channel (postMessage/websocket).

**`draft_connection`** (Job B): `draft_id`, `current_step`, `completed_steps[]`,
`resumable_until`.

**`job`** (Job C bulk + agent-triggered): `job_id`, `type: seed|reseed|verify|revoke`,
`state`, per-item `progress`, `needs_human: bool`, `error`.

**cross-cutting:** secret-free audit feed; an **Action-Required inbox** + notify
hooks (email/push); `viewer_scope: self|admin`, `is_medical: bool`, step-up token
state.

## 9. Build phasing (value-per-effort order)

| Phase | Ships | Why first |
|---|---|---|
| **0 — read model + auth** | `connection` read API (list/status/health) + OIDC sign-in + *My accounts* **read-only** | Smallest backend; instantly answers "what's connected?"; proves auth. |
| **1 — Live hand-off (Job A)** | `live_view_handle` endpoint + the Live Stage + re-seed from a row | Relieves the only unavoidable human pain; wraps existing noVNC. |
| **2 — Connect wizard (Job B)** | draft connection + the 5-step wizard + capability review + binding write-back | Composes Phase 1 + a one-line policy change. |
| **3 — inventory actions + queue (Job C full)** | revoke (step-up), bulk re-seed `job` queue, Action-Required inbox, proactive expiry notify | The self-service + scale layer. |
| **cross-cutting** | step-up/sudo, Admin surface, secret-free audit explorer | Threaded through every phase, not a final bolt-on. |

Phase 0 alone is a meaningful product (a read-only "are my sessions alive?"
dashboard). Each phase is independently shippable.

## 10. Stack

Mirror the workspace's established admin-UI pattern for consistency and reuse:
**React + shadcn/ui + Tailwind + Storybook + Playwright** (the same stack as
`sideport-ui-experience`). Note from research: **there is no official shadcn
`Stepper`** — hand-roll one (Apple/Linear/Stripe all do). Keep behavior and visual
styling separate; default to quiet color, one decision per screen, generous spacing.

## 11. Hand-off to the design agent

When this plan is greenlit, the following prompt drives the **Sideport UI Designer**
(IA/flows/wireframes/Storybook story list) and then the **Sideport UI Implementer**:

> Design the **Tessera self-hosted admin portal** — calm, Apple-like,
> single-household-to-small-team — for a secretless credential broker whose promise
> is **"we hold the logged-in session; we never reveal the password or cookie."**
> Three surfaces in shadcn/ui + Tailwind (hand-roll the stepper). Behavior and
> visual styling separate; quiet color; one decision per screen.
>
> **A) Live hand-off** — in-portal embedded remote-browser stage (Browserbase
> Live-View model: `<iframe>` read/write, toggleable chrome, listen for a
> `tessera-session-done`/`disconnected` postMessage) + a pop-out escape hatch.
> Top strip = verified target hostname + favicon + lock (anti-phishing anchor);
> right rail = 3-item task list; a status pill (`Connecting → Waiting for you →
> Verifying → Done`); a visible countdown on a short-TTL session;
> **server-detected success auto-advances** (manual "I'm done" fallback); graceful
> **expiry → re-arm**; an out-of-band "Action required" nudge. Persistent trust
> line. Never a raw noVNC URL, never an infinite spinner.
>
> **B) Connect-account wizard** (`pick provider → name the person → seed (launches
> A) → verify → done`). Resumable draft connection; "Resume — 1 step left" card;
> the async seed shows the live window/real status (Vercel-deploy feel), not fake
> progress; a capability-review finish ("as Alice the agent may *read*, not
> *pay*"); a real verify probe before "connected."
>
> **C) Accounts inventory** (per person; "Alice's accounts"). Read-mostly table:
> provider · person · health badge (`Live / Expiring / Absent / Error`) · last
> re-seeded · expires (honest "~estimated/unknown") · row actions (Re-seed → A,
> Revoke, View activity). **No reveal affordance for secret values** — *"Tessera
> can't show this — that's the point."* Detail drawer = metadata + bundle-field
> presence only + secret-free audit timeline. Bulk "Re-seed all expiring" queue
> with per-item `Queued → Needs human → Live`.
>
> **Cross-cutting:** OIDC/SSO-first sign-in (no local admin password); step-up
> re-auth (sudo-mode) + type-the-name-to-confirm for Revoke and any
> medical/cross-person action; two separate surfaces — "My accounts" (self) vs
> "Admin / All connections" (operator, extra-audited, stricter for medical).
> Deliver as Storybook stories (calm light/dark) + Playwright happy-path flows.
