# Tessera Admin Portal — UI Design Spec & Wireframes

> **Status:** Design-phase. Implements the UI for
> [ADR 0016 — Admin portal](../adr/0016-admin-portal.md) and
> [specs/admin-portal.md](../specs/admin-portal.md). **Do not contradict those two
> documents** — this spec is downstream of them and only adds IA, wireframes, state
> matrices, component mapping, copy, and a Storybook/Playwright plan.
>
> **This is design only.** No production React. Everything below is buildable by an
> implementer using **React + shadcn/ui + Tailwind + Storybook + Playwright**.
> Behavior is described separately from visual styling on purpose.

---

## 0. First principles (the non-negotiables this UI exists to express)

Tessera is a **self-hosted, secretless credential broker**. Its promise:

> **"We hold the logged-in browser session. We never reveal the password or cookie."**

The portal is a **thin convenience layer**, never a source of truth:

- **Files stay the source of truth** — the portal *reads* recipes and *proposes*
  grants/bindings as reviewable diffs.
- **The broker still decides** — default-deny lives in the broker, never in the UI.
- **Secret values are NEVER shown** — no reveal, no copy, no value in any response,
  toast, log, or audit line. The UI says so out loud:
  *"Tessera can't show this — that's the point."*

**Audience:** a single household → small team (≤ ~10 people). **One admin
(operator), the rest are members.**

**Visual tone:** Apple-calm. Quiet color (red reserved for *true* error only). One
decision per screen. Generous spacing. Light + dark, both first-class.

**Example data used throughout (generic only):**

- People: `alice@example.com` (**Admin** / operator), `bob@example.com` (Member),
  `carol@example.com` (Member).
- Providers (recipes): Health Portal (`portal.example-health.com`), Utility Co
  (`pay.example-utility.com`), Marketplace (`shop.example.com`), Webmail
  (`mail.example.com`).

**Visual tokens (styling, kept separate from behavior):**

| Token | Light | Dark | Used for |
|---|---|---|---|
| `--surface` | zinc-50 | zinc-950 | app background |
| `--card` | white | zinc-900 | tables, drawers, dialogs |
| `--text` | zinc-900 | zinc-100 | primary text |
| `--muted` | zinc-500 | zinc-400 | secondary / "~estimated" |
| `health.live` | emerald-600 | emerald-400 | Live badge |
| `health.expiring` | amber-600 | amber-400 | Expiring soon |
| `health.absent` | zinc-400 | zinc-500 | Absent (neutral, not alarming) |
| `health.error` | red-600 | red-400 | **Error only** |
| `accent` | indigo-600 | indigo-400 | primary CTA, "Verifying" |

> Behavior rule that overrides styling: **Absent is neutral gray, not red.** Red is
> only ever `error`. An un-seeded account is a to-do, not a failure.

---

## A. Information architecture / navigation map

### A.1 Route table

| Route | Surface | Scope | Gate |
|---|---|---|---|
| `/sign-in` | OIDC sign-in (Microsoft) | public | — |
| `/` | redirect → `/accounts` | self | authed |
| `/accounts` | **My accounts** (default landing) | self | authed |
| `/accounts/:connectionId` | Connection **detail drawer** (overlay on `/accounts`) | self | authed |
| `/connect` | **Connect-account wizard** (`?step=1..5`, `/connect/:draftId` to resume) | self | authed |
| `/action-required` | **Action-required inbox** | self | authed |
| `/handoff/:handleId` | **Live hand-off stage** (also opened inline from wizard step 3 / a row's Re-seed) | self | authed + short-TTL handle |
| `/admin/users` | **Users** list (people + roles) | admin | in admins allow-list |
| `/admin/users/:personId` | **Person detail** — that person's accounts (scoped) | admin | step-up (cross-person) |
| `/admin/connections` | **Admin · All connections** | admin | **step-up to enter** |
| `/admin/queue` | Refresh / re-seed queue (bulk jobs) | admin | admin |
| `/admin/audit` | Secret-free audit explorer | admin | admin |

> **Two separate surfaces, not a role flag on one view.** `My accounts` (self) and
> `Admin · All connections` (operator) are distinct routes. Admin entry is
> **step-up gated** and extra-audited.

### A.2 Sidebar (persistent left nav, desktop)

```
┌──────────────────┐
│ ▦ Tessera         │   ← wordmark + environment badge ("homelab")
│   homelab          │
├──────────────────┤
│ MINE              │
│ ● My accounts      │   /accounts        (default)
│ ◔ Action required 1│   /action-required (count badge when > 0)
│                  │
│ ADMIN             │   ← whole section hidden unless principal ∈ admins allow-list
│ ⚙ Users           │   /admin/users
│ 🔒 All connections │   /admin/connections   (🔒 = step-up required to enter)
│ ↻ Refresh queue    │   /admin/queue
│ ▤ Audit            │   /admin/audit
├──────────────────┤
│ alice@example.com ▾│   ← identity chip (avatar + preferred_username)
│ ◐ Theme     ⏻ Sign │      theme toggle · sign out
└──────────────────┘
```

- **Members never see the ADMIN section** (it isn't rendered — not greyed out).
- The lock glyph (🔒) on **All connections** signals step-up *before* the click,
  not after.
- The identity chip is the same verified `preferred_username` the broker keys
  grants/bindings on (sign-in identity == brokered identity).

### A.3 IA tree

```
Tessera portal  (OIDC sign-in — Microsoft /common)
│
├── My accounts (self)                       ← default landing
│     ├── connection rows  [provider · health · last re-seeded · expires · ⋯]
│     ├── Connect account ▸  → Connect wizard
│     └── connection detail drawer  (metadata · presence flags · audit · policy)
│
├── Action required (self)                    ← inbox of paused / blocked seeds
│     └── Live hand-off stage  (the captcha surface)
│
└── Admin (operator only; step-up)
      ├── Users               (people + role + #connections + #needs-attention)
      │     └── Person detail (their My-accounts table, scoped)
      ├── All connections     (everyone; owner column; extra-audited)
      ├── Refresh queue       (bulk re-seed jobs; per-item progress)
      └── Audit               (secret-free event explorer)
```

---

## Flow summary (the three jobs + cross-cutting)

1. **Know what's connected (Job C).** Land on *My accounts* → read health at a
   glance → open a row's drawer for metadata/presence/audit/policy → act via `⋯`
   (Re-seed / Revoke / View activity). No secret value anywhere.
2. **Seed a session (Job A — the crown jewel).** Trigger Re-seed / wizard step 3 →
   pre-flight dialog → **Live hand-off stage** (embedded remote browser, target
   strip, 3-item task list, status pill, countdown) → person logs in + solves
   captcha → **server detects success → auto-advances** → Verifying → Done. Graceful
   expiry → re-arm. Leave-and-resume lands in *Action required*.
3. **Connect an account (Job B).** Hand-rolled 5-step stepper: pick provider →
   name the person → seed (launches Job A, resumable) → verify (real probe) → done
   (capability review). Abandoned drafts surface as a "Resume — 1 step left" card.
4. **Operate (admin).** *Users* shows people derived from OIDC principals + the
   admins allow-list. Open a member → their scoped accounts. *All connections* is a
   separate, step-up-gated, extra-audited surface. Destructive/medical/cross-person
   actions require step-up; Revoke also requires type-the-name-to-confirm.

---

## Screen list (index)

| # | Screen | Route | Key states wireframed |
|---|---|---|---|
| 1 | Sign-in | `/sign-in` | Microsoft · Error · SignedOut |
| 2 | App shell / nav | (all) | MemberNav · AdminNav · Mobile · Dark |
| 3 | My accounts table | `/accounts` | Empty · MixedHealth · AllExpiring · Loading · Error · Mobile · RowMenu |
| 4 | Connection detail drawer | `/accounts/:id` | Live · Absent · Error · presence-flags · audit |
| 5 | Connect wizard | `/connect` | Step1–5 · Resume |
| 6 | Live hand-off | `/handoff/:id` | Preflight · Connecting · WaitingForYou · Verifying · Done · Reconnecting · Expired · Paused · Error · Mobile |
| 7 | Action-required inbox | `/action-required` | OneItem · Empty |
| 8 | Users list | `/admin/users` | AdminAndMembers · NeedsAttention |
| 9 | Person detail | `/admin/users/:id` | MemberAccounts · Empty |
| 10 | Admin · All connections | `/admin/connections` | StepUpRequired · Table |
| 11 | Refresh queue | `/admin/queue` | Mixed · AllDone |
| 12 | Step-up modal | (overlay) | EnterAdmin · RevokeConfirm · Medical |
| 13 | Revoke confirm | (overlay) | TypeName · Disabled |

---

## B. ASCII wireframes

> Desktop-first. Mobile reflow is called out per screen and consolidated in the
> **Mobile behavior** section. Frames are schematic — spacing/typography are the
> implementer's per the visual tokens, not literal pixel boxes.

### 1. Sign-in — `SignIn/Microsoft`

```
┌──────────────────────────────────────────────────────────┐
│                                                          │
│                        ▦  Tessera                         │
│                                                          │
│            Sign in to manage your connected accounts.     │
│                                                          │
│            ┌──────────────────────────────────────┐      │
│            │   ⊞  Sign in with Microsoft           │      │  ← only auth path
│            └──────────────────────────────────────┘      │
│                                                          │
│      Tessera never stores a password.                     │
│      Your sessions stay in your own vault.                │  ← trust footer
│                                                          │
└──────────────────────────────────────────────────────────┘
```

`SignIn/Error` — same frame, inline alert above the button:
*"That account isn't allowed here. Ask your operator to add you."* (allow-list miss)
or *"Sign-in didn't complete. Try again."* (OIDC failure). **No local password
fallback ever.**

### 2. App shell (see A.2). `AppShell/MemberNav` hides the ADMIN section entirely.

### 3. My accounts — `AccountsTable/MixedHealth`

```
┌──────────────────────────────────────────────────────────────────────┐
│ My accounts                                       [ + Connect account ]│
│ ──────────────────────────────────────────────────────────────────── │
│ ⚠ 2 accounts need attention.            [ Re-seed all expiring (1) ]   │  ← banner only if needed
│                                                                      │
│ PROVIDER          HEALTH         LAST RE-SEEDED   EXPIRES              │
│ ─────────────────────────────────────────────────────────────────────│
│ 🏥 Health Portal   ● Live         12 days ago      ~estimated      ⋯   │
│ 🏦 Utility Co       ◐ Expiring 2d  88 days ago      in 2 days       ⋯   │
│ 🛒 Marketplace      ○ Absent       —                —               ⋯   │
│ 📨 Webmail          ⚠ Error        3 days ago       —               ⋯   │
│ ─────────────────────────────────────────────────────────────────────│
│ 4 connections · acting as alice@example.com                           │
└──────────────────────────────────────────────────────────────────────┘
```

`AccountsTable/RowMenuOpen` — the `⋯` dropdown:

```
        ┌──────────────────────┐
        │ ↻  Re-seed            │   → pre-flight → Live hand-off
        │ ▤  View activity      │   → detail drawer (Activity tab)
        │ ──────────────────── │
        │ ⊘  Revoke…            │   (destructive; type-to-confirm)
        └──────────────────────┘
```

> For an **Absent** row the primary action in the menu is **Seed now** (not
> Re-seed), and the row's right edge can show a quiet **[ Seed ]** button.

`AccountsTable/Empty` — `EmptyState/FirstAccount`:

```
┌──────────────────────────────────────────────────────────────────────┐
│ My accounts                                                           │
│ ──────────────────────────────────────────────────────────────────── │
│                                                                      │
│                          ▦  (calm illustration)                       │
│                                                                      │
│                     Connect your first account                        │
│                                                                      │
│      Tessera holds a logged-in session so an agent can act for        │
│      you — without ever seeing your password.                         │
│                                                                      │
│                     [ + Connect account ]                             │
│                                                                      │
└──────────────────────────────────────────────────────────────────────┘
```

`AccountsTable/Loading` — 4 skeleton rows (no spinner-only screen).
`AccountsTable/Error` — inline alert *"Couldn't load your accounts. Retry."* +
`[ Retry ]`; never an infinite spinner.

### 4. Connection detail drawer — `ConnectionDrawer/Live` (right-side Sheet)

```
                          ┌───────────────────────────────────────┐
                          │ 🏥 Health Portal               ✕       │
                          │ acting as alice@example.com            │
                          │ ● Live · verified 12 days ago          │
                          ├───────────────────────────────────────┤
                          │ ┌─ Overview ─┬─ Activity ─┬─ Policy ─┐ │  ← Tabs
                          │ │                                    │ │
                          │ │ Target                             │ │
                          │ │   🔒 portal.example-health.com      │ │
                          │ │   login: /account/sign-in          │ │
                          │ │                                    │ │
                          │ │ Session contents                   │ │
                          │ │   ✓ has cookies                    │ │  ← PRESENCE only
                          │ │   ✓ has refresh token              │ │
                          │ │   — no access token                │ │
                          │ │                                    │ │
                          │ │   Tessera can't show this —         │ │  ← the line, verbatim
                          │ │   that's the point.                 │ │
                          │ │                                    │ │
                          │ │ Health                             │ │
                          │ │   last re-seeded   12 days ago      │ │
                          │ │   last used        3 hours ago      │ │
                          │ │   expires          ~estimated       │ │
                          │ │   seed method      live hand-off    │ │
                          │ └────────────────────────────────────┘ │
                          ├───────────────────────────────────────┤
                          │ [ ↻ Re-seed ]            [ ⊘ Revoke… ] │
                          └───────────────────────────────────────┘
```

`ConnectionDrawer/Live` → Activity tab (`ConnectionDrawer/AuditTimeline`):

```
                          │ Activity (secret-free)                 │
                          │  ● 3h ago   used by agent · allowed    │
                          │  ● 12d ago  re-seeded · alice (human)  │
                          │  ● 12d ago  verified live · broker     │
                          │  ● 41d ago  re-seeded · alice (human)  │
                          │  ● 41d ago  created · alice (human)    │
```

`ConnectionDrawer/Absent` — Session contents shows `— no cookies / — no refresh
token`, the never-reveal line still present, footer primary is **[ Seed now ]**.
`ConnectionDrawer/Error` — top shows `⚠ Error` + a one-line reason
(*"Last check failed: session rejected by provider."*), footer primary **[ ↻
Re-seed ]**.

> **No reveal control exists in any tab.** There is no eye icon, no "show", no
> copy button. Presence flags are the entire story of "what's inside".

### 5. Connect-account wizard

`ConnectWizard/Step1PickProvider`:

```
┌──────────────────────────────────────────────────────────────────────┐
│ Connect an account                                            ✕        │
│ ──────────────────────────────────────────────────────────────────── │
│  ●─────○─────○─────○─────○                                            │  ← hand-rolled Stepper
│  Provider  Person  Seed  Verify  Done                                 │
│                                                                      │
│  Which account?                                                       │
│  ┌──────────────┐                                                     │
│  │ 🔎 Search…    │                                                     │
│  └──────────────┘                                                     │
│  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐                  │
│  │ 🏥 Health │ │ 🏦 Utility│ │ 🛒 Market │ │ 📨 Webmail│   ← recipe tiles│
│  │   Portal  │ │   Co     │ │   place   │ │           │                │
│  └──────────┘ └──────────┘ └──────────┘ └──────────┘                  │
│                                                                      │
│  Don't see it?  Request a recipe →                                    │  ← does NOT author
│                                                                      │
│                                              [ Cancel ]  [ Next → ]   │
└──────────────────────────────────────────────────────────────────────┘
```

`ConnectWizard/Step2NamePerson`:

```
│  ●─────●─────○─────○─────○                                            │
│  Provider  Person  Seed  Verify  Done                                 │
│                                                                      │
│  Who is this account for?                                             │
│  ┌──────────────────────────────────────────────┐                    │
│  │ ○ alice@example.com  (you)                     │                    │
│  │ ○ bob@example.com                              │                    │
│  │ ○ carol@example.com                            │                    │
│  │ ＋ Someone else…                                │                    │
│  └──────────────────────────────────────────────┘                    │
│  This is who the agent will act as. It does not share a password.     │
│                                              [ ← Back ]  [ Next → ]    │
```

> People listed here are **derived from OIDC principals already in policy**, in
> plain language — never YAML, never a free-text identity field by default.

`ConnectWizard/Step3Seeding` — the async hard step, launches Job A inline:

```
│  ●─────●─────◍─────○─────○        a draft is saved — you can close this │
│  Provider  Person  Seed  Verify  Done    window and resume later.      │
│                                                                      │
│  ┌─────────────── Live hand-off (embedded) ───────────────┐          │
│  │ 🔒 portal.example-health.com · ⏱ 1:47 · ⤢ Pop out       │          │
│  │  [ live remote browser — read/write ]   │ ① Log in ✓    │          │  ← real window,
│  │                                        │ ② Checkbox ●   │          │     NOT a fake bar
│  │                                        │ ③ We take over │          │
│  │  We keep the session. Never your password.             │          │
│  └────────────────────────────────────────────────────────┘          │
│                                       [ Close — resume later ]        │
```

`ConnectWizard/Step4Verify` — a **real** probe (never claim connected without it):

```
│  ●─────●─────●─────◍─────○                                            │
│                                                                      │
│             ⟳  Checking the session is really live…                   │  ← real read-only self-test
│                                                                      │
│  We're confirming Tessera can use this session, before we say it's    │
│  connected.                                                           │
```
`ConnectWizard/Step4VerifyFailed`: `⚠ We couldn't confirm the session.` +
`[ ← Re-seed ]` (back to step 3) / `[ Get help ]`.

`ConnectWizard/Step5Done` — `CapabilityReview/ReadOnly`:

```
│  ●─────●─────●─────●─────●                                            │
│                                                                      │
│             ✓  Health Portal is connected.                            │
│                                                                      │
│  As bob@example.com, the agent may:                                   │
│     ✓ read appointments                                               │
│  It may not:                                                          │
│     ✗ pay      ✗ book      ✗ change the account                       │
│                                                                      │
│  You can revoke this anytime from My accounts.                        │
│                                       [ View connection → ]           │
```

> The "may / may not" list is **server-derived from the actual grants
> (default-deny)** — not hardcoded copy. See backend gaps.

`ConnectWizard/Resume` — `ResumeCard/OneStepLeft` (shown on *My accounts* /
*Action required* when a draft exists):

```
┌────────────────────────────────────────────────────────┐
│ ◍ Resume — 1 step left                                  │
│ Health Portal for bob@example.com · saved 6 min ago     │
│ You stopped at: Seed the session.                       │
│                         [ Discard ]   [ Resume → ]      │
└────────────────────────────────────────────────────────┘
```

### 6. Live hand-off (the crown jewel)

`LiveHandoff/Preflight` (Dialog, before launch):

```
┌──────────────────────────────────────────────────┐
│  Connect Health Portal                             │
│  ────────────────────────────────────────────────│
│  This takes about 2 minutes.                       │
│                                                  │
│   • You'll log in to Health Portal and solve       │
│     the checkbox.                                  │
│   • We keep the logged-in session —                │
│     we never see your password.                    │
│   • If it times out, you can start again.          │
│                                                  │
│                       [ Cancel ]   [ Start ]       │
└──────────────────────────────────────────────────┘
```

`LiveHandoff/WaitingForYou` (full stage):

```
┌──────────────────────────────────────────────────────────────────────┐
│ 🔒 portal.example-health.com — verified   ·  ⏱ 4:38 left  ·  ⤢ Pop out │  ← target-identity strip
├──────────────────────────────────────────────────────┬───────────────┤
│                                                      │  What to do     │
│                                                      │  ① Log in   ●   │  ← 3-item task list
│              [  live remote browser                  │  ② Solve the    │
│                 read / write · 16:10  ]              │     checkbox    │
│                                                      │  ③ We take      │
│                                                      │     over    ✓   │
│                                                      │                │
│                                                      │  ● Waiting for  │  ← status pill
│                                                      │     you         │
├──────────────────────────────────────────────────────┴───────────────┤
│ We keep the session. We never see your password.        [ I'm done ]  │  ← persistent trust line
└──────────────────────────────────────────────────────────────────────┘                + manual fallback
```

- **Target-identity strip** = favicon + **verified hostname** + lock. The
  anti-phishing anchor; Tessera chrome is clearly *around* the browser, never *as*
  it.
- **`[ I'm done ]`** is the *manual fallback*. Success is normally **server-detected
  and auto-advances** — the button never gates the flow.

`LiveHandoff/Connecting` — canvas is a skeleton, pill `● Connecting…`, task list
greyed, countdown not yet ticking, `[ Cancel ]` available.

`LiveHandoff/Verifying` — canvas dims with an overlay, pill `● Verifying…`
(indigo), countdown **frozen**, no actions needed (brief, auto):

```
│              [ canvas dimmed ]      ● Verifying…                      │
│            We're confirming the session is live.                      │
```

`LiveHandoff/Done`:

```
┌──────────────────────────────────────────────────────────────────────┐
│                            ✓  Session live                             │
│        Health Portal is connected for alice@example.com.              │
│                                                                      │
│                     [ Back to My accounts → ]                         │
└──────────────────────────────────────────────────────────────────────┘
```

`LiveHandoff/Reconnecting` — last frame frozen + overlay, pill `● Reconnecting…`
(amber, pulsing), countdown **keeps running** (honest), `[ Pop out ]` still there.

`LiveHandoff/Expired` (graceful re-arm — never a blank noVNC error):

```
┌──────────────────────────────────────────────────────────────────────┐
│                            ⏱  This window timed out                    │
│              No problem — starting again is quick.                     │
│                                                                      │
│                          [ Start again ]                              │
└──────────────────────────────────────────────────────────────────────┘
```

`LiveHandoff/Paused` (shows as an *Action required* row after leave-and-resume):

```
│ ◔ Paused — resume within 7:42                                         │
│ Health Portal · alice@example.com                  [ Resume → ]       │
```

`LiveHandoff/Error` — pill `● Couldn't verify` (red), panel with one-line reason +
`[ Try again ]` / `[ Get help ]`. **No raw worker URL is ever shown.**

`LiveHandoff/LowTimeWarning` — at `< 1:00` the countdown turns amber and the strip
reads `⏱ 0:48 left` with a subtle emphasis (no modal interruption).

### 7. Action-required inbox — `ActionRequiredInbox/OneItem`

```
┌──────────────────────────────────────────────────────────────────────┐
│ Action required                                                       │
│ ──────────────────────────────────────────────────────────────────── │
│ ◔ Finish login for Health Portal                                      │
│   alice@example.com · paused 1 min ago · resume within 7:42           │
│                                              [ Resume login → ]       │
└──────────────────────────────────────────────────────────────────────┘
```
`ActionRequiredInbox/Empty`: *"Nothing needs you right now."* (calm, not a spinner).

### 8. Users (admin) — `UsersList/AdminAndMembers`

```
┌──────────────────────────────────────────────────────────────────────┐
│ Users                                                                 │
│ ──────────────────────────────────────────────────────────────────── │
│ People are derived from your sign-in directory and policy.            │
│ Admins are a small allow-list, not a database.                        │
│                                                                      │
│ PERSON                         ROLE      CONNECTIONS   NEEDS ATTENTION │
│ ─────────────────────────────────────────────────────────────────────│
│ alice@example.com  (you)        Admin     4             1        →     │
│ bob@example.com                 Member    2             0        →     │
│ carol@example.com               Member    1             1        →     │
└──────────────────────────────────────────────────────────────────────┘
```

`UsersList/NeedsAttention` — the "Needs attention" cell turns amber when > 0.

### 9. Person detail (admin → a member) — `PersonDetail/MemberAccounts`

```
┌──────────────────────────────────────────────────────────────────────┐
│ ← Users   ·   bob@example.com   ·   Member                            │
│ ──────────────────────────────────────────────────────────────────── │
│ Viewing another person's accounts is step-up gated and audited.       │  ← cross-person notice
│                                                                      │
│ PROVIDER          HEALTH         LAST RE-SEEDED   EXPIRES              │
│ ─────────────────────────────────────────────────────────────────────│
│ 🏥 Health Portal   ● Live         2 days ago       ~estimated      ⋯   │
│ 🏦 Utility Co       ● Live         9 days ago       ~estimated      ⋯   │
└──────────────────────────────────────────────────────────────────────┘
```

> This is **the same `AccountsTable`, scoped to one owner**. It is *not* a merged
> all-people view — that only exists under `/admin/connections`.

`PersonDetail/Empty`: *"bob@example.com has no connections yet."*

### 10. Admin · All connections — `AdminAllConnections/StepUpRequired` (entry gate)

```
┌──────────────────────────────────────────────────────────────────────┐
│                          🔒  Admin area                                │
│        All connections shows every person's accounts.                 │
│        Confirm it's you to continue. This visit is audited.           │
│                       [ Continue with Microsoft ]                     │
└──────────────────────────────────────────────────────────────────────┘
```

`AdminAllConnections/Table` (after step-up; **owner column present**):

```
┌──────────────────────────────────────────────────────────────────────┐
│ All connections                            [ filter: owner ▾ health ▾ ]│
│ ──────────────────────────────────────────────────────────────────── │
│ OWNER              PROVIDER       HEALTH        EXPIRES           ⋯    │
│ ─────────────────────────────────────────────────────────────────────│
│ alice@example.com  🏥 Health Portal ● Live       ~estimated       ⋯    │
│ alice@example.com  🏦 Utility Co     ◐ Expiring   in 2 days        ⋯    │
│ bob@example.com    🏥 Health Portal ● Live       ~estimated       ⋯    │
│ carol@example.com  📨 Webmail       ⚠ Error      —                ⋯    │
└──────────────────────────────────────────────────────────────────────┘
```

### 11. Refresh queue (admin) — `RefreshQueue/Mixed`

```
┌──────────────────────────────────────────────────────────────────────┐
│ Refresh queue · Re-seed all expiring (3)                              │
│ ──────────────────────────────────────────────────────────────────── │
│ 🏦 Utility Co · alice      ● Live           done                       │
│ 🏥 Health Portal · carol   ◔ Needs human    [ Finish login → ]         │  ← captcha → Job A
│ 📨 Webmail · alice         ◍ Queued         waiting…                   │
│ ─────────────────────────────────────────────────────────────────────│
│ 1 done · 1 needs you · 1 queued                                       │
└──────────────────────────────────────────────────────────────────────┘
```

### 12. Step-up / sudo modal

`StepUpModal/EnterAdmin`, `StepUpModal/RevokeConfirm`, `StepUpModal/Medical`
(Dialog):

```
┌──────────────────────────────────────────────────┐
│  Confirm it's you                                  │
│  ────────────────────────────────────────────────│
│  This is a sensitive action. Sign in again to      │
│  continue.                                         │
│                                                  │
│                  [ Continue with Microsoft ]       │
└──────────────────────────────────────────────────┘
```
`StepUpModal/Medical` adds a line: *"This is a medical connection — extra
confirmation is required."* and (optional) a dual-control note.

### 13. Revoke confirm — `RevokeConfirm/TypeName` (AlertDialog, type-to-confirm)

```
┌──────────────────────────────────────────────────┐
│  Revoke Health Portal for alice@example.com?       │
│  ────────────────────────────────────────────────│
│  This deletes the stored session. The agent can    │
│  no longer act on this account until you re-seed    │
│  it. This can't be undone.                         │
│                                                  │
│  Type the provider name to confirm:                │
│  ┌──────────────────────────────────────────┐     │
│  │ Health Portal                              │     │
│  └──────────────────────────────────────────┘     │
│                                                  │
│                  [ Cancel ]   [ Revoke connection ]│  ← destructive; disabled
└──────────────────────────────────────────────────┘     until exact match
```

`RevokeConfirm/Disabled` — confirm button disabled, helper text *"Type the name
exactly to enable."* For a **medical** connection the flow is **step-up →
type-to-confirm** (two gates).

---

## C. State matrices

### C.1 Connection health (the row / badge state machine)

| State | Badge (glyph · label · token) | Meaning / trigger | "Last re-seeded" cell | "Expires" cell | Primary action | `⋯` menu | Notes |
|---|---|---|---|---|---|---|---|
| **Live** | `●` Live · `health.live` | Session valid; last probe passed | relative ("12 days ago") | honest `~estimated` / `unknown` / concrete | Re-seed | Re-seed · View activity · Revoke | calm; the default good state |
| **Expiring soon** | `◐` Expiring 2d · `health.expiring` | Within threshold OR estimated-and-old | relative | `in 2 days` | **Re-seed** (emphasized) | Re-seed · View activity · Revoke | amber, not red |
| **Absent** | `○` Absent · `health.absent` | No bundle: never seeded or revoked | `—` | `—` | **Seed now** → Job A | Seed now · View activity · Remove | **neutral gray**, it's a to-do |
| **Error** | `⚠` Error · `health.error` | Last probe failed / bundle rejected | relative | `—` | **Re-seed** | Re-seed · View activity · Revoke | one-line reason in drawer; only red state |
| **Seeding** | `◍` Seeding · `accent` | A seed job is running now | "in progress" | `—` | **Open window** → Live stage | Open window · Cancel | live, not a fake bar |
| **Needs human** | `◔` Needs you · `health.expiring` (pulse) | Queued job blocked on captcha | relative or `—` | `—` | **Finish login** → Live stage | Finish login · Cancel | surfaces in Action required + queue |

> **Expires honesty rule:** cookies often have no readable TTL. Render exactly one
> of `~estimated`, `unknown`, `in N days/hours`, or `—` (Absent). Always pair with
> **last used** in the drawer for trust. Never fabricate a precise date.

### C.2 Live hand-off status machine

| State | Pill (label · token) | Canvas | Countdown | Task list | Available actions | Auto-transition (server event) | Manual transition |
|---|---|---|---|---|---|---|---|
| **Connecting** | `● Connecting…` · neutral | skeleton | not started | greyed | Cancel | iframe ready → **Waiting for you** | — |
| **Waiting for you** | `● Waiting for you` · amber | live, read/write | **running** | ①→②→③ active | Pop out · Cancel · *I'm done* (fallback) | liveness signal → **Verifying** | *I'm done* → Verifying |
| **Verifying** | `● Verifying…` · accent | dimmed + overlay | **frozen** | ③ checking | — (brief) | probe ok → **Done**; probe fail → **Error** | — |
| **Done** | `● Done` · `health.live` | success panel | hidden | all ✓ | Continue / Back to accounts | — | Continue |
| **Reconnecting** | `● Reconnecting…` · amber (pulse) | last frame frozen + overlay | **keeps running** | unchanged | Pop out | socket back → **Waiting for you**; TTL hit → **Expired** | — |
| **Expired** | `● Expired` · neutral | re-arm panel | `0:00` | reset | Start again | — | Start again → new handle → **Connecting** |
| **Paused** | (no pill; row in Action required) | — | resume window ticking | — | Resume | resume → new handle → **Connecting** | Resume |
| **Error** | `● Couldn't verify` · `health.error` | error panel | hidden | — | Try again · Get help | — | Try again → **Connecting** |

**PostMessage event contract the iframe wrapper must handle (design assumption — see
gaps):** `tessera-session-ready`, `tessera-session-done`,
`tessera-session-disconnected`, `tessera-session-expired`,
`tessera-session-error`. **Server-detected success drives Verifying/Done** — the
manual *I'm done* button is a fallback, never required.

---

## D. Storybook story list

> One story = one reachable state, wired to fixture props (no live backend). Build
> the hard-to-reach states (Expired, Error, Reconnecting, NeedsHuman) **first**.

**SignIn**
- `SignIn/Microsoft` — OIDC landing, single Microsoft button + trust footer.
- `SignIn/Error/NotAllowed` — principal not in allow-list message.
- `SignIn/Error/OidcFailed` — generic "didn't complete, try again".
- `SignIn/SignedOut` — explicit signed-out confirmation.

**AppShell**
- `AppShell/MemberNav` — sidebar without ADMIN section.
- `AppShell/AdminNav` — with ADMIN section + 🔒 on All connections.
- `AppShell/MobileNav` — collapsed nav (sheet / bottom bar).
- `AppShell/Dark` — dark theme parity.

**AccountsTable**
- `AccountsTable/Empty` — "Connect your first account".
- `AccountsTable/SingleLive` — one healthy row.
- `AccountsTable/MixedHealth` — Live + Expiring + Absent + Error rows.
- `AccountsTable/AllExpiring` — banner + "Re-seed all expiring".
- `AccountsTable/Loading` — skeleton rows (no spinner-only).
- `AccountsTable/Error` — load failed + Retry.
- `AccountsTable/RowMenuOpen` — `⋯` dropdown open on a Live row.
- `AccountsTable/RowMenuAbsent` — Absent row menu (Seed now primary).
- `AccountsTable/Mobile` — stacked cards grouped by health.

**ConnectionDrawer**
- `ConnectionDrawer/Live` — full metadata, all presence flags green.
- `ConnectionDrawer/Absent` — empty presence flags, Seed CTA.
- `ConnectionDrawer/Error` — error banner + reason.
- `ConnectionDrawer/PresenceFlags` — focuses the presence block + never-reveal line.
- `ConnectionDrawer/AuditTimeline` — secret-free Activity tab.
- `ConnectionDrawer/Policy` — the grants/bindings backing it, plain language.
- `ConnectionDrawer/Mobile` — full-screen sheet.

**LiveHandoff**
- `LiveHandoff/Preflight` — the ~2-minute dialog.
- `LiveHandoff/Connecting` — warming, skeleton canvas.
- `LiveHandoff/WaitingForYou` — live canvas + task list + countdown.
- `LiveHandoff/LowTimeWarning` — countdown < 1:00, amber.
- `LiveHandoff/Verifying` — dimmed canvas, frozen countdown.
- `LiveHandoff/Done` — success panel.
- `LiveHandoff/Reconnecting` — frozen frame + overlay.
- `LiveHandoff/Expired` — re-arm panel.
- `LiveHandoff/Paused` — Action-required row form.
- `LiveHandoff/Error` — couldn't verify.
- `LiveHandoff/PoppedOut` — placeholder when the session is in a tab.
- `LiveHandoff/Mobile` — full-width canvas, collapsed task list, pop-out nudge.

**TargetIdentityStrip**
- `TargetIdentityStrip/Verified` — favicon + verified host + lock + countdown.
- `TargetIdentityStrip/HostMismatch` — defensive anti-phishing warning state.

**TaskList**
- `TaskList/Step1Active` · `TaskList/Step2Active` · `TaskList/AllDone`.

**StatusPill**
- `StatusPill/Connecting` · `WaitingForYou` · `Verifying` · `Done` ·
  `Reconnecting` · `Expired` · `Error` — one per token.

**Countdown** (hand-rolled)
- `Countdown/Normal` (4:38) · `Countdown/Warning` (<1:00) · `Countdown/Expired` (0:00).

**ConnectWizard**
- `ConnectWizard/Step1PickProvider` — tiles + search.
- `ConnectWizard/Step1Search` — filtered + "Request a recipe".
- `ConnectWizard/Step1NoMatch` — empty search result.
- `ConnectWizard/Step2NamePerson` — pick/create person.
- `ConnectWizard/Step3Seeding` — embedded Live stage, async.
- `ConnectWizard/Step3PausedResume` — closeable draft.
- `ConnectWizard/Step4Verify` — real probe running.
- `ConnectWizard/Step4VerifyFailed` — probe failed, retry.
- `ConnectWizard/Step5Done` — capability review.
- `ConnectWizard/Mobile` — compact stepper header.

**Stepper** (hand-rolled)
- `Stepper/FiveSteps` — desktop, step 3 active.
- `Stepper/WithCompleted` — completed checks + current + upcoming.
- `Stepper/Compact` — mobile "Step 3 of 5" + bar.

**ResumeCard**
- `ResumeCard/OneStepLeft` — "Resume — 1 step left".
- `ResumeCard/Dismissible` — discard affordance.

**CapabilityReview**
- `CapabilityReview/ReadOnly` — "may read · may NOT pay/book".
- `CapabilityReview/Medical` — stricter, medical badge.

**UsersList**
- `UsersList/AdminAndMembers` — alice Admin, bob + carol Member.
- `UsersList/NeedsAttention` — amber attention counts.
- `UsersList/SingleAdmin` — just the operator.
- `UsersList/Mobile` — stacked.

**PersonDetail**
- `PersonDetail/MemberAccounts` — bob's scoped accounts + cross-person notice.
- `PersonDetail/Empty` — member with no connections.

**AdminAllConnections**
- `AdminAllConnections/StepUpRequired` — entry gate.
- `AdminAllConnections/Table` — everyone's connections + owner column.
- `AdminAllConnections/Filtered` — by owner / health.

**RefreshQueue**
- `RefreshQueue/Mixed` — Queued / Needs human / Live.
- `RefreshQueue/AllDone` · `RefreshQueue/WithError`.

**ActionRequiredInbox**
- `ActionRequiredInbox/OneItem` · `ActionRequiredInbox/Empty` ·
  `ActionRequiredInbox/Multiple`.

**StepUpModal**
- `StepUpModal/EnterAdmin` · `StepUpModal/RevokeConfirm` · `StepUpModal/Medical` ·
  `StepUpModal/CrossPerson`.

**RevokeConfirm**
- `RevokeConfirm/TypeName` — input + matched.
- `RevokeConfirm/Disabled` — confirm disabled until exact match.
- `RevokeConfirm/Medical` — step-up + type-to-confirm combined.

**HealthBadge**
- `HealthBadge/Live` · `ExpiringSoon` · `Absent` · `Error` · `Seeding` · `NeedsHuman`.

**RoleBadge**
- `RoleBadge/Admin` · `RoleBadge/Member`.

**ExpiryCell**
- `ExpiryCell/Estimated` · `Unknown` · `Concrete` · `LastUsedPaired`.

**Toast** (Sonner)
- `Toast/SessionLive` · `Toast/ReseedQueued` · `Toast/Revoked` ·
  `Toast/HandleExpired` · `Toast/ProbeFailed`.

---

## E. Component inventory (screen → shadcn/ui)

| Screen / element | shadcn/ui (or hand-rolled) |
|---|---|
| App shell + sidebar | layout primitives, `Separator`, `Avatar`, `Button`, `Tooltip`, `Badge` (env), `DropdownMenu` (identity chip), theme toggle |
| Sign-in | `Card`, `Button`, `Alert` |
| My accounts / Person detail / All connections | `Table` (+ TanStack Table for sort/filter), `Badge` (HealthBadge), `DropdownMenu` (row `⋯`), `Button`, `Alert` (banner), `Skeleton` (loading), `Input`/`Select` (admin filters) |
| Connection detail | `Sheet` (drawer), `Tabs` (Overview/Activity/Policy), `Badge`, `Separator`, `ScrollArea` (audit), `Button` |
| Connect wizard | `Dialog` **or** route page; **hand-rolled `Stepper`**; `Command`/`Input` (provider search), `Card` (recipe tiles), `RadioGroup` (person), `Button` |
| Live hand-off | **hand-rolled `LiveViewIframe` wrapper** (postMessage), **hand-rolled `Countdown`**, `Badge` (StatusPill), `Dialog` (pre-flight), `Card` (Done/Expired/Error panels), `Button` (Pop out / I'm done / Start again) |
| Task list (right rail) | list primitives + `Badge`/check glyphs (hand-rolled, trivial) |
| Action-required inbox | `Card`/list, `Badge`, `Button` |
| Users list | `Table`, `Badge` (RoleBadge), `Button` |
| Step-up / sudo | `Dialog`, `Button` (OIDC continue) |
| Revoke confirm | `AlertDialog`, `Input` (type-to-confirm), `Button` (destructive) |
| Notifications | `Sonner` (toasts) |
| Empty/loading/error states | `Skeleton`, `Alert`, simple illustration + `Button` |

**Hand-rolled (no official shadcn equivalent):** `Stepper`, `Countdown`,
`LiveViewIframe` wrapper, `StatusPill` (a thin `Badge` variant), `TaskList`,
`HealthBadge` (a `Badge` variant table).

---

## F. Copy deck (exact microcopy)

**Global trust lines**
- Live stage (persistent): **"We keep the session. We never see your password."**
- Inventory / drawer never-reveal: **"Tessera can't show this — that's the point."**
- Sign-in footer: **"Tessera never stores a password. Your sessions stay in your
  own vault."**

**Sign-in**
- Title: **"Tessera"** · Subhead: **"Sign in to manage your connected accounts."**
- CTA: **"Sign in with Microsoft"**
- Not-allowed: **"That account isn't allowed here. Ask your operator to add you."**
- OIDC failure: **"Sign-in didn't complete. Try again."**

**Empty state (My accounts)**
- Title: **"Connect your first account"**
- Body: **"Tessera holds a logged-in session so an agent can act for you — without
  ever seeing your password."**
- CTA: **"Connect account"**

**Captcha pre-flight (dialog)**
- Title: **"This takes about 2 minutes."**
- Bullets:
  - **"You'll log in to {provider} and solve the checkbox."**
  - **"We keep the logged-in session — we never see your password."**
  - **"If it times out, you can start again."**
- CTAs: **"Cancel"** · **"Start"**

**Live stage**
- Strip: **"{hostname} — verified"** · **"⏱ {m:ss} left"** · **"Pop out"**
- Task list: **"① Log in"** · **"② Solve the checkbox"** · **"③ We take over"**
- Pills: **"Connecting…" / "Waiting for you" / "Verifying…" / "Done" /
  "Reconnecting…" / "Expired" / "Couldn't verify"**
- Manual fallback: **"I'm done"**
- Verifying caption: **"We're confirming the session is live."**
- Done: **"Session live"** · **"{provider} is connected for {person}."**
- Expired: **"This window timed out."** · **"No problem — starting again is quick."**
  · CTA **"Start again"**
- Paused (Action required): **"Paused — resume within {m:ss}"** · CTA **"Resume"**
- Error: **"We couldn't verify the session."** · CTAs **"Try again"** · **"Get help"**

**Out-of-band nudge (email/push)**
- Subject: **"Finish connecting {provider}"**
- Body: **"{person}'s {provider} needs a quick login to stay connected. It takes
  about 2 minutes."**
- CTA: **"Finish now"**

**Wizard**
- Step 1: **"Which account?"** · search placeholder **"Search…"** ·
  **"Don't see it? Request a recipe."**
- Step 2: **"Who is this account for?"** ·
  **"This is who the agent will act as. It does not share a password."**
- Step 3: **"A draft is saved — you can close this window and resume later."** ·
  **"Close — resume later"**
- Step 4: **"Checking the session is really live…"** ·
  **"We're confirming Tessera can use this session, before we say it's connected."**
- Step 4 fail: **"We couldn't confirm the session."** · CTA **"Re-seed"**
- Capability review: **"{provider} is connected."** ·
  **"As {person}, the agent may:"** {grants} ·
  **"It may not:"** {denials} ·
  **"You can revoke this anytime from My accounts."**

**Resume card**
- **"Resume — 1 step left"** · **"{provider} for {person} · saved {ago}"** ·
  **"You stopped at: {step}."** · CTAs **"Discard"** · **"Resume"**

**Users (admin)**
- Helper: **"People are derived from your sign-in directory and policy. Admins are
  a small allow-list, not a database."**
- Cross-person notice: **"Viewing another person's accounts is step-up gated and
  audited."**

**Step-up modal**
- Title: **"Confirm it's you"** ·
  Body: **"This is a sensitive action. Sign in again to continue."** ·
  CTA: **"Continue with Microsoft"**
- Medical add-on: **"This is a medical connection — extra confirmation is required."**

**Revoke confirm (type-to-confirm)**
- Title: **"Revoke {provider} for {person}?"**
- Body: **"This deletes the stored session. The agent can no longer act on this
  account until you re-seed it. This can't be undone."**
- Field label: **"Type the provider name to confirm:"** ·
  Helper (disabled): **"Type the name exactly to enable."**
- CTAs: **"Cancel"** · **"Revoke connection"**

**Toasts**
- **"Session live."** · **"Re-seed queued."** · **"Connection revoked."** ·
  **"That window expired — start again."** · **"Couldn't verify the session."**

---

## G. Anti-pattern checklist (the implementer MUST NOT violate)

1. **No reveal control, anywhere.** No "show password", eye icon, or value field for
   any cookie/token. Presence flags (`✓ has cookies`) are the only story.
2. **No copy-to-clipboard** on any secret-adjacent field.
3. **No infinite spinner.** Every async has a live feed (the canvas), a visible
   countdown, or a real probe with a timeout and an explicit error state.
4. **No raw noVNC / worker URL exposed.** The live browser is always wrapped in
   Tessera chrome (target strip + trust line). Never link to or print the bare
   worker handle.
5. **No fake/synthetic progress bar** during the seed. Show the real window and real
   status; verify with a real probe, not a timed animation.
6. **No mixing people's accounts** in one view. *My accounts* is strictly self.
   Cross-person lives only under `/admin/connections` (+ owner column, step-up,
   extra audit) or a step-up-gated `/admin/users/:personId`.
7. **No forced "done" click we could detect.** Server-detected success
   auto-advances; *I'm done* is a fallback only.
8. **No "connected" without a verify probe.** Never claim success before the
   read-only self-test passes.
9. **No persistent god-mode.** Admin entry is step-up; the sudo window is
   short-lived; every admin / cross-person action is audited.
10. **No destructive action without type-to-confirm.** Revoke requires typing the
    provider name; medical adds per-action re-auth (two gates).
11. **No Tessera page that mimics the target.** Chrome is clearly *around* the
    browser, never *as* it (anti-phishing).
12. **No secret in any toast, error, log, or audit line.** Audit is metadata-only.
13. **No surprise expiry.** Countdown is always visible on short-TTL sessions;
    expiry degrades to a friendly re-arm, never a blank error.
14. **No role flag toggling one view.** Self and admin are separate routes.
15. **No local password field, ever.** OIDC is the only sign-in path.
16. **No YAML dumped on a household member.** Grants/bindings/recipes never surface
    as raw config; capability review is plain language.
17. **Absent ≠ Error.** Absent is neutral gray (a to-do); red is reserved for true
    error.

---

## Mobile behavior (reflow notes)

- **Nav** collapses to a hamburger `Sheet` (or bottom tab bar): My accounts ·
  Action required · (Admin). Identity + theme move into the sheet footer.
- **Tables → cards.** `AccountsTable`, `UsersList`, `AllConnections` render as
  stacked cards grouped by health (worst first). Each card: provider/owner, health
  badge, expires, one primary action; `⋯` becomes a bottom action sheet.
- **Detail drawer** (`Sheet`) becomes a **full-screen** sheet.
- **Live hand-off** prioritizes the canvas: full-width browser, the 3-item task
  list collapses into a top accordion or a thin strip, the countdown pins to the
  top strip, and **Pop out is emphasized** (small screens + clipboard quirks).
- **Wizard** is already one-decision-per-step; the stepper becomes a compact
  `Step 3 of 5` label + a progress bar (`Stepper/Compact`).
- **Modals** (step-up, revoke) become bottom sheets; type-to-confirm input stays
  above the keyboard.

---

## API dependencies (screen → backend resource)

These resources are defined in [specs/admin-portal.md §8](../specs/admin-portal.md).
The UI consumes them read-mostly and proposes changes as reviewable files.

| Screen | Resource(s) | Fields the UI binds |
|---|---|---|
| My accounts / Person detail / All connections | `connection` (list) | `display_name`, `recipe_id`/provider, `target.favicon_url`, `owner_person_id`, `status`, `last_seeded_at`, `last_used_at`, `expires_at`, `expiry_is_estimated` |
| Connection drawer | `connection` (get) | + `has_cookies`, `has_refresh_token`, `has_access_token`, `target.hostname/login_url`, `seed_method`, policy linkage (`grants`/`bindings`) |
| Connection drawer → Activity | secret-free **audit feed** (scoped to connection) | `{actor, action, decision, target, person, timestamp, jti}` |
| Live hand-off | `live_view_handle` | `live_view_url` (short-TTL, single-use), `mode`, `session_ttl_seconds`, `expires_at`, `liveness_signal`, event channel (`postMessage`/ws) |
| Connect wizard | `recipe` (list, read-only), `draft_connection` | `recipes[]`; `draft_id`, `current_step`, `completed_steps[]`, `resumable_until` |
| Wizard step 4 (Verify) | broker **read-only self-test** | pass/fail + reason |
| Wizard step 5 (capability review) | grant projection for the new binding | `may[]` / `may_not[]` derived from default-deny grants |
| Refresh queue | `job` | `job_id`, `type`, `state`, per-item `progress`, `needs_human`, `error` |
| Action-required inbox | `job` (needs_human) + paused drafts/handles + **notify hooks** | inbox items + deep links |
| Users list | derived people endpoint | person (`oid`/`preferred_username`), role (admins allow-list), `#connections`, `#needs_attention` |
| Step-up / admin gates | step-up token / sudo-window state | `viewer_scope: self|admin`, `is_medical`, sudo expiry |

---

## Backend gaps (labeled — the UI assumes these; they are NOT yet guaranteed)

> Per mode rules these are **flagged as gaps**, not invented capabilities.

- **[R1 — known]** `live_view_handle` needs a worker `act()` channel the roadmap
  defers. **Phase 1 wraps the existing noVNC/harvester** (`sessionkeeper`) as the
  first browser worker; cookie still only goes worker→vault. The UI is agnostic to
  which worker serves the handle.
- **[R2 — known]** Auto-advance assumes a **reliable per-provider liveness signal**.
  It may be best-effort per recipe → the manual *I'm done* path must **always** ship
  and must be first-class, not hidden. The status machine already treats auto as the
  happy path and manual as a peer fallback.
- **[R3 — known]** Wizard binding write-back reintroduces "UI mutates policy".
  **Default to a GitOps PR** (wizard *proposes* a diff); a hot admin-API write only
  behind step-up + its own audit event. The UI must show whether a completed wizard
  produced a **merged change** or a **pending PR** (don't imply instant effect).
- **[new — favicon/hostname trust]** The anti-phishing target strip depends on a
  **server-provided, trustworthy** `target.hostname` + `favicon_url`. If the backend
  can't yet guarantee these, the strip must degrade to hostname-only (never a
  user-supplied or recipe-spoofable favicon).
- **[new — `last_used_at`]** The drawer/table show **"last successfully used"** for
  trust; confirm the projection populates it (spec lists it; verify it's wired).
- **[new — postMessage contract]** The exact event names
  (`tessera-session-done` / `-disconnected` / `-expired` / `-error`) must be agreed
  with the worker before the `LiveViewIframe` wrapper is built.
- **[new — capability-review data]** The "may / may not" list must be **derived from
  real grants**, server-side. Until that projection exists, the wizard's step 5 is a
  placeholder, not shippable.
- **[new — derived people + roles endpoint]** Users view needs an endpoint that
  returns OIDC principals classified by the admins allow-list with connection +
  attention counts. **No DB** (spec §7b) — it's a projection.
- **[new — notify hooks]** The out-of-band nudge (email/push deep link) is Phase 3;
  the inbox works without it but the "biggest win over SSH" requires it.

---

## Playwright screenshot checklist

Capture each at **desktop (1280)** and **phone (390)**, in **light and dark**:

- [ ] `SignIn/Microsoft`
- [ ] `AccountsTable/Empty`
- [ ] `AccountsTable/MixedHealth` (all four health states visible)
- [ ] `AccountsTable/AllExpiring` (banner + bulk CTA)
- [ ] `ConnectionDrawer/Live` (presence flags + never-reveal line on screen)
- [ ] `ConnectionDrawer/AuditTimeline`
- [ ] `LiveHandoff/Preflight`
- [ ] `LiveHandoff/WaitingForYou` (target strip + countdown + task list + trust line)
- [ ] `LiveHandoff/Verifying`
- [ ] `LiveHandoff/Done`
- [ ] `LiveHandoff/Expired` (re-arm)
- [ ] `ConnectWizard/Step1PickProvider`
- [ ] `ConnectWizard/Step3Seeding`
- [ ] `ConnectWizard/Step5Done` (capability review)
- [ ] `ResumeCard/OneStepLeft`
- [ ] `UsersList/AdminAndMembers`
- [ ] `PersonDetail/MemberAccounts` (cross-person notice visible)
- [ ] `AdminAllConnections/StepUpRequired`
- [ ] `RefreshQueue/Mixed`
- [ ] `StepUpModal/RevokeConfirm`
- [ ] `RevokeConfirm/TypeName` (confirm disabled until exact match)

**Happy-path flows (Playwright):**
1. Sign in → My accounts (read-only) → open drawer → assert **no reveal control
   exists** and the never-reveal line is present.
2. Re-seed a row → pre-flight → Live stage `WaitingForYou` → simulate
   `tessera-session-done` → `Verifying` → `Done` → toast "Session live".
3. Wizard end-to-end: provider → person → seed (resume once) → verify (real probe)
   → capability review.
4. Admin: step-up gate → All connections → revoke (type-to-confirm) → audited toast.

---

## Next implementation slice (with acceptance criteria)

Aligns with **Phase 0** in [specs/admin-portal.md §9](../specs/admin-portal.md)
(read model + auth) — the smallest shippable, meaningful product.

**Slice: "Are my sessions alive?" — read-only My accounts + OIDC sign-in.**

Build: `SignIn`, `AppShell` (member nav), `AccountsTable` (Empty / MixedHealth /
Loading / Error / Mobile), `ConnectionDrawer` (Live / Absent / Error, read-only),
`HealthBadge`, `ExpiryCell`, theme toggle. All wired to the `connection` read API.

Acceptance criteria:
- [ ] Unauthenticated users hit `/sign-in`; the only auth path is "Sign in with
      Microsoft"; **no password field exists** in the DOM.
- [ ] `/accounts` lists the signed-in person's connections only (self scope);
      another principal's data is never fetched or rendered.
- [ ] Each row shows provider, a correct 4-state health badge, last re-seeded
      (relative), and an **honest** expires cell (`~estimated` / `unknown` /
      concrete / `—`).
- [ ] The drawer shows presence flags (`✓/—`) and the line **"Tessera can't show
      this — that's the point."**; there is **no reveal or copy control anywhere**
      (asserted by Playwright).
- [ ] Empty, loading (skeleton), and error (retry, **no infinite spinner**) states
      all render.
- [ ] Light and dark parity; mobile reflow to grouped cards.
- [ ] Storybook covers every state above; Playwright captures the screenshot
      checklist subset for this slice.

> Re-seed, the Live hand-off stage, the wizard, and admin surfaces are explicitly
> **out of this slice** (Phases 1–3) but their entry points (`⋯ → Re-seed`,
> `+ Connect account`) may render as disabled/"coming soon" affordances or be
> hidden — implementer's choice, but never a dead control that silently no-ops.
