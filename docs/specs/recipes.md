# Spec — Recipes

> Status: **draft** (design phase). A recipe is the easy-setup unit: "how do I
> connect *this* provider?" It is declarative, contains **no secrets**, and is the
> only thing most users ever author besides grants.

A recipe binds three things together for one provider:

1. **how its credential is obtained** (which harvest driver, or "none — it's an
   API key / OAuth you supply"),
2. **how the broker acts with it** (`http` injection vs `browser`/`app` egress),
3. **where the credential lives** (the store secret name) and **what scopes** make
   sense.

## Why recipes (and what they are not)

- A recipe is **provider knowledge**, not a credential. Secrets always live in the
  store; recipes reference them by name. A recipe is safe to share/commit *if* it
  contains no real account identifiers.
- Recipes that reveal a *specific person's* accounts (e.g. naming a particular
  health provider for a named user) belong in a **private deployment overlay**, not
  in the public catalogue.
- Public, shippable recipes are generic templates: `oauth`, `api-key`,
  `cookie-session`, plus well-known public providers (e.g. `google`).

## Recipe shape (illustrative — final schema set during build)

```toml
# recipes/google.toml — an OAuth provider; no harvester needed.
[recipe]
name      = "google"
kind      = "oauth"          # oauth | api-key | cookie-session | driven
egress    = "http"           # http | browser | app
[recipe.oauth]
token_url = "https://oauth2.googleapis.com/token"
scopes    = ["https://www.googleapis.com/auth/calendar.readonly"]
# the refresh/access tokens are resolved from the store by the binding below
```

```toml
# recipes/health-portal.toml — un-API'd; only a human login. Harvested + injected.
[recipe]
name    = "health-portal"
kind    = "cookie-session"
egress  = "http"             # cookie can be replayed over HTTP
[recipe.harvest]
driver  = "browser"          # browser | android | desktop  (see harvest-drivers.md)
login_url = "https://portal.example.com/login"
# selectors/flow live in the driver's private recipe overlay, never here
[recipe.refresh]
strategy = "silent-http"     # silent-http | relogin
```

```toml
# recipes/app-only.toml — a cert-pinned mobile app; cannot be replayed over HTTP.
[recipe]
name   = "app-only-provider"
kind   = "driven"
egress = "app"               # the broker dispatches a scoped act() to the worker
[recipe.harvest]
driver = "android"           # future driver (ADR 0006)
```

## Fields

| Field | Meaning |
|---|---|
| `kind` | `oauth` · `api-key` · `cookie-session` · `driven` |
| `egress` | `http` (YARP injection — common) · `browser`/`app` (driven by a worker) |
| `harvest.driver` | which harvest driver obtains/keeps the session (`browser`/`android`/`desktop`); omitted for `oauth`/`api-key` |
| `refresh.strategy` | `silent-http` (cheap) or `relogin` (drives the driver, rate-limited) |
| binding (in `grants`) | which store secret backs `(target, on_behalf_of)` — see the grants/target binding model |

## Relationship to grants

- A **recipe** says *how a provider works*.
- A **grant** says *who may act on it* (caller, optional end-user, target, actions).
- A **binding** says *which stored secret* backs a `(target, on_behalf_of)` pair.

A provider is "connected" when it has a recipe + a binding; it is "usable" when a
grant authorizes a caller and the bundle resolves `present`.

## Open questions

- Final schema (TOML vs YAML), and whether selectors live in a separate
  private-overlay file referenced by the public recipe.
- Recipe signing/verification (treat third-party recipes as untrusted input).
- A `recipes/` catalogue layout + contribution rules (no PII, generic only).
