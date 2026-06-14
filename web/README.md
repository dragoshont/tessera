# Tessera Admin Portal — `web/`

The frontend for the Tessera self-hosted admin portal. Tessera is a **secretless
credential broker**: it holds a logged-in browser session so an agent can act for
you, and it **never reveals the password or cookie**. This portal is a thin,
read-mostly window onto that model — never a source of truth.

> Design authority (don't contradict): [`docs/ui/tessera-admin-portal-ui-spec.md`](../docs/ui/tessera-admin-portal-ui-spec.md),
> [`docs/specs/admin-portal.md`](../docs/specs/admin-portal.md),
> [`docs/adr/0016-admin-portal.md`](../docs/adr/0016-admin-portal.md).

## Scope — Phase 0

A runnable shell with **two real views** over a typed, mockable data client (an
in-memory fixture; no backend yet) plus the design-system primitives:

- **My accounts** (`/accounts`, default landing) — the signed-in person's
  connections: provider · health badge (Live / Expiring soon / Absent / Error) ·
  last re-seeded · honest expiry · row `⋯` menu. Includes the empty state and a
  connection **detail drawer** showing **bundle-field presence only**
  (has cookies ✓ / has refresh token ✓) and the line
  *"Tessera can't show this — that's the point."* — **no reveal/copy affordance
  for any secret value, anywhere.**
- **Users** (`/admin/users`) — people derived from OIDC principals + the admins
  allow-list: the operator shows as **Admin**, the others as **Members**.

Clean routes/placeholders are left for later phases (live captcha hand-off,
connect wizard, action-required inbox, all-connections step-up gate).

## Stack

Vite + React + TypeScript · Tailwind CSS v4 · shadcn/ui + Radix primitives ·
TanStack Query + TanStack Table · Storybook · Vitest + Testing Library ·
Playwright. Light/dark theming. Behavior is kept separate from visual styling.

## Commands

```bash
npm install
npm run dev              # Vite dev server
npm run build            # tsc -b && vite build (type-checked production build)
npm run test             # Vitest unit tests (jsdom)
npm run build-storybook  # build the Storybook static site
npm run storybook        # Storybook dev server on :6006
npm run lint             # ESLint

# End-to-end / screenshots (downloads a browser first):
npx playwright install chromium
npm run test:e2e         # smoke checks (sign-in → accounts → drawer → users)
npx playwright test screenshots   # capture screens at desktop 1280 + phone 390, light + dark
```

## Layout

```
web/
  src/
    api/        typed Tessera client (in-memory) + TanStack Query hooks
    app/        session (static sign-in) provider
    components/
      accounts/ AccountsTable, ConnectionDrawer, presence flags, empty state
      badges/   HealthBadge, RoleBadge
      shell/    AppShell (sidebar + top bar + mobile nav)
      sign-in/  SignIn
      theme/    theme provider + toggle (light/dark)
      ui/       shadcn-style primitives (button, badge, table, sheet, tabs, …)
      users/    UsersList
    data/       contract types + fixtures (alice/bob/carol — generic only)
    pages/      route containers (+ later-phase placeholders)
  tests/        Playwright smoke + screenshot specs
```

## Data contract

Field names mirror the .NET `connection` projection (`docs/specs/admin-portal.md`
§8). There is **no secret-value field** in the contract — only presence booleans
(`hasCookies` / `hasRefreshToken` / `hasAccessToken`), so a secret is structurally
impossible to render.

## Note on the fixtures

`connectionCount` is **derived** from the fixture connections so the two views can
never disagree. Alice therefore shows **4** connections (one per health state — the
slice requires all four visible, and the authoritative spec wireframe shows alice
with 4), rather than the "2" mentioned in passing in the build brief. Roles match
the acceptance criterion exactly (alice = Admin; bob, carol = Members) and
`needsAttentionCount` is mocked to 0.
