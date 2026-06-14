# Spec — Recipes

> Status: **implemented** for HTTP-injectable providers ([ADR 0014](../adr/0014-http-injectable-provider-egress.md));
> `browser`/`app` egress is still design-phase. A recipe is the easy-setup unit:
> "how do I connect *this* provider?" It is declarative, contains **no secrets**,
> and is the only thing most users author besides grants.

A recipe binds three things together for one provider:

1. **how its credential is obtained** (which harvest driver, or "none — it's an
   API key / OAuth you supply"),
2. **how the broker acts with it** (`http` injection vs `browser`/`app` egress),
3. **where the credential lives** (the store secret name, via the binding) and
   **what tools/actions** it exposes.

## Why recipes (and what they are not)

- A recipe is **provider knowledge**, not a credential. Secrets always live in the
  store; recipes reference them by name. A recipe is safe to share/commit *if* it
  contains no real account identifiers.
- Recipes that reveal a *specific person's* accounts (e.g. naming a particular
  health provider for a named user) belong in a **private deployment overlay**, not
  in the public catalogue.
- Public, shippable recipes are generic templates: `oauth`, `api-key`,
  `cookie-session`, plus well-known public providers.

## Recipe shape (the implemented JSON schema)

A recipe lives in the policy document (`grants.json`) under `recipes[]`. This is
the **exact** shape the broker parses ([`Recipe.cs`](../../src/Tessera.Core/Recipes/Recipe.cs),
[`LoadedPolicy.cs`](../../src/Tessera.Core/Configuration/LoadedPolicy.cs)). Nothing
provider-specific is hardcoded in the broker — a new site is **config only**.

```jsonc
{
  "target": "health-portal",              // logical provider name (grants/bindings key on this)
  "driver": "browser",                    // how the session is SEEDED (browser|android|desktop); informational for http egress
  "egress": "http",                       // "http" = Tessera calls the upstream; "none" = status-only (no call)
  "upstreamBaseUrl": "https://api.health-portal.example.com/v1",
  "injection": "cookies",                 // none | bearer | cookies — how the credential is attached
  // For cookie sessions: map each cookie NAME to where its value comes from in
  // the stored bundle (access_token | refresh_token | cookie:<name>). Lets a
  // portal that carries its session as named cookies be fed from the bundle.
  "cookieMap": {
    "SessionId": "access_token",
    "RefreshId": "refresh_token"
  },
  // Static, non-secret headers on every call. Values may interpolate:
  //   {extra:KEY} — a per-account field from the bundle's "extra" map (vault)
  //   {env:NAME}  — a process env var (a provider-wide key projected from a Secret)
  "extraHeaders": {
    "X-Api-Key": "{env:HEALTH_PORTAL_APIM_KEY}",
    "User-Agent": "Mozilla/5.0 (…)"
  },
  "actions": ["read:appointments", "write:book"],   // the action verbs this recipe exposes
  "tools": [                              // one MCP tool per entry
    {
      "name": "list_appointments",        // MCP tool name
      "method": "GET",
      "path": "appointments",             // appended to upstreamBaseUrl
      "action": "read:appointments",      // the grant action this tool needs
      "description": "List the signed-in person's appointments."
    },
    {
      "name": "book_appointment",
      "method": "POST",
      "path": "appointments",
      "action": "write:book",
      "stepUp": true,                     // WRITE → requires an explicit confirm=true (human-in-the-loop)
      "description": "Book an appointment (echo the slot back and get a yes first)."
    }
  ],
  "description": "Health portal — read appointments; step-up booking."
}
```

A **bearer** provider is simpler: `"injection": "bearer"` and omit `cookieMap`
(the bundle's `access_token` becomes `Authorization: Bearer …`). A **status-only**
recipe (no upstream call) is `"egress": "none"` with no `tools`.

## Fields

| Field | Type | Meaning |
|---|---|---|
| `target` | string | Logical provider name. Grants + bindings key on this. |
| `driver` | string | How the session is **seeded** (`browser`/`android`/`desktop`). Informational for `http` egress; the harvester reads it. |
| `egress` | string | `http` → Tessera performs the upstream call; `none` → status-only (resolve + report, **no** call). |
| `upstreamBaseUrl` | string | Base URL; each tool's `path` is appended (`base.TrimEnd('/') + '/' + path.TrimStart('/')`). |
| `injection` | string | `none` · `bearer` (→ `Authorization: Bearer <access_token>`) · `cookies` (→ `Cookie:` header). |
| `cookieMap` | map | Cookie name → bundle source (`access_token` · `refresh_token` · `cookie:<name>`). When set, the `Cookie` header is built from these named sources. |
| `extraHeaders` | map | Static headers on every call. Values may use `{extra:KEY}` (per-account vault field) or `{env:NAME}` (process env). |
| `actions` | string[] | Action verbs this recipe exposes (drives the surface). |
| `tools[]` | object[] | Each: `name`, `method`, `path`, `action`, optional `stepUp` (write → confirm-gated), `description`. |
| binding (in `grants`) | — | Which store secret backs `(target, on_behalf_of)` — see below. |

### Safety properties (enforced by the broker, not the recipe)

- **SSRF allow-list:** the recipe's host must be in `egress.allowedHosts`
  ([`SsrfGuard`](../../src/Tessera.Core/Egress/SsrfGuard.cs)); HTTPS only.
- **Step-up writes:** any `stepUp` tool (or a `write:*` grant action) returns a
  step-up decision and runs only with `confirmed=true`.
- **No token passthrough:** the inbound caller token is **not** forwarded upstream;
  only the injected provider cookie/bearer is sent.
- **Inject, never hand over:** the caller never sees the credential — only the result.

## Relationship to grants

- A **recipe** says *how a provider works*.
- A **grant** says *who may act on it* (`caller`, optional `onBehalfOf` end-user,
  `target`, `actions`, optional `stepUpActions`).
- A **binding** says *which stored secret* backs a `(target, onBehalfOf)` pair.

A provider is "connected" when it has a recipe + a binding; it is "usable" when a
grant authorizes a caller and the bundle resolves `present`.

### Multi-account on one site

Two people on the same site = two bindings + two grants, keyed on the verified
principal (`oid` or `preferred_username`). Each user's call resolves *their* secret:

```jsonc
"bindings": [
  { "target": "health-portal", "onBehalfOf": "alice@example.com", "credential": "health-portal-alice" },
  { "target": "health-portal", "onBehalfOf": "bob@example.com",   "credential": "health-portal-bob" }
],
"grants": [
  { "caller": "chat://librechat", "onBehalfOf": "alice@example.com", "target": "health-portal", "actions": ["read:*"] },
  { "caller": "chat://librechat", "onBehalfOf": "bob@example.com",   "target": "health-portal", "actions": ["read:*"] }
]
```

## Open questions

- A public `recipes/` catalogue layout + contribution rules (no PII, generic only).
- Recipe signing/verification (treat third-party recipes as untrusted input).
- `browser`/`app` egress recipe fields (selectors live in a private overlay).
