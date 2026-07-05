# OAuth-MCP fronting — Tessera 2.0-beta implementation & rollout plan

Grounds **[ADR 0027](../adr/0027-broker-fronts-oauth-mcp-upstreams.md)**. Phased,
gate-checked, and **plan-only for infrastructure** (Tessera proposes; a human
applies via Flux — [ADR 0018](../adr/0018-access-gateway-and-action-broker.md) /
repo Architrave contract). Nothing here materializes a secret or runs an
apply-shaped command.

Branch: `2.0-beta`. Nothing in this document is merged to `main` until the gates in
§6 are green and a human has reviewed.

---

## 0. Conformance target — `mobbin-clone-mcp`

The full acquire → store → inject → front → refresh loop is built and verified
against **`mobbin-clone-mcp`** (separate repo), a self-hosted OAuth-MCP that
reproduces the real Mobbin tool surface with **synthetic** data:

- tools `search_flows` / `search_screens` / `search_sections` — byte-identical
  schemas + verbatim descriptions to the live Mobbin `tools/list`;
- inline image content + metadata, each result carrying a `mobbin_url`;
- `401 + WWW-Authenticate: Bearer resource_metadata="…"` (RFC 9728);
- an optional `MCP_FREE_TIER_TOKEN` → `402` gate that reproduces Mobbin's free-tier
  paywall, for a token-swap (free → entitled) demo;
- accepts the credential as `Authorization: Bearer` **or** `Cookie: session=` so the
  same fixture exercises both the OAuth-bearer and the harvest-cookie paths.

Because it needs no paid plan and no access to the private corporate MCP, it is the
deterministic fixture for every egress/refresh test below, and the live demo target.

## 1. The classifier (onboarding aid, read-only)

A single probe decides the recipe shape and is safe to run during connect:

```bash
curl -sS -i -X POST "$MCP_URL" \
  -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}' \
  | grep -iE '^HTTP/|www-authenticate'
```

- `401` + `WWW-Authenticate: Bearer resource_metadata=…` ⇒ **class 1, OAuth-MCP** →
  the `oauth-mcp` recipe shape (§2/§3).
- anything else ⇒ **class 2** → the existing harvest-and-inject recipe (`Cookies` /
  `ApiKeyHeader` / `Basic`).

## 2. Code seams (where each change lands)

| Change | File(s) | Note |
|---|---|---|
| Recipe declares an OAuth-MCP upstream | `src/Tessera.Core/Recipes/Recipe.cs` | add an `Upstream`/`UpstreamKind` (e.g. `oauth-mcp`) + `Resource` + `Scopes`; **additive**, no change to `InjectionKind` |
| Policy parse | `src/Tessera.Core/Configuration/LoadedPolicy.cs` | parse the new recipe fields (mirrors `ParseInjection`) |
| RFC 9728 / 8414 discovery | `src/Tessera.Providers/OAuthMcp/Discovery.cs` (new) | fetch protected-resource metadata → AS metadata; allow-list-scoped |
| Acquisition driver (auth-code + PKCE + refresh) | `src/Tessera.Providers/OAuthMcp/` (new driver, [ADR 0006](../adr/0006-harvest-drivers.md)) | user round-trip once (connect wizard), refresh thereafter; writes bundle tokens |
| Single-writer refresh | reuse `src/Tessera.Providers/…` rotation + [ADR 0026](../adr/0026-single-writer-rotation-lease-and-fencing.md) | no double-refresh |
| Injection | `src/Tessera.Broker/Egress/CredentialInjector.cs` | **reused** — `InjectionKind.BearerToken` |
| Egress fronting the upstream MCP | `src/Tessera.Mcp/…` ([ADR 0015](../adr/0015-mcp-egress-through-tessera.md)) | proxy the streamable-HTTP JSON-RPC; inject per-principal bearer |
| Audience/resource guard | `src/Tessera.Broker/Egress/InjectionEgress.cs` | refuse to inject a token to any host but its bound `resource` |

## 3. Phases (each ends green on its gate)

- **P1 — discovery + recipe shape (read-only).** Add the `oauth-mcp` recipe fields +
  RFC 9728/8414 discovery + the classifier. Gate: unit tests resolve
  `mobbin-clone`'s AS from a `WWW-Authenticate` challenge; no token yet.
- **P2 — acquisition + store.** Auth-code + PKCE once (connect wizard), refresh
  after; store per-principal; single-writer. Gate: acquire against `mobbin-clone`
  (using its dev token as the AS stand-in, or a local AS), refresh round-trips,
  rotation is single-writer.
- **P3 — MCP egress fronting.** Front `mobbin-clone`'s `/mcp` through Tessera.Mcp,
  injecting the per-user bearer; audience guard on. Gate: `tools/list` + each tool
  returns inline images + metadata **through Tessera**, client holds no token.
- **P4 — conformance.** A parity test asserts the brokered surface equals
  `mobbin-clone` direct (same tools, schemas, `mobbin_url`s). Gate: parity + the
  free→entitled token-swap demo (402 → 200) via a Tessera-side entitlement change.

## 4. Homelab deployment — PLAN-ONLY (human applies via Flux)

Proposed, not applied. Manifests are sketches for review; a human commits + `flux
reconcile`.

1. **`mobbin-clone` app** — `Deployment` + `Service` + `Ingress` under `apps/…`,
   image pinned to a **semver** tag (repo preference: versions, not git-sha), env
   `MCP_EXPECTED_TOKEN` sourced from a sealed/Key Vault secret (never inline).
2. **Tessera recipe** (`oauth-mcp`) for the `mobbin-clone` target + a `chat://librechat`
   grant + the per-user binding; discovery points at the in-cluster `mobbin-clone`.
3. **Chat wiring** — add the brokered tool to LibreChat's cached tool list and (if
   voice is in scope) the voice allow-list; `rollout restart` the affected deploys
   (no config reloader).
4. **Edge** — Cloudflare DNS + cert-manager cert + a `NetworkPolicy` scoping egress
   to the upstream resource only.

Blast radius is high; this section stays a **plan** until reviewed and applied by a
human.

## 5. Security invariants (non-negotiable)

- The upstream token is **never** returned to the client/model — inject-only
  ([ADR 0014](../adr/0014-http-injectable-provider-egress.md)).
- A token is **audience-bound** to its RFC 9728 `resource`; egress refuses any other
  host (confused-deputy / SSRF guard, layered on the per-hop allow-list).
- Refresh-token rotation is **single-writer** ([ADR 0026](../adr/0026-single-writer-rotation-lease-and-fencing.md));
  no throwaway process ever calls refresh against the live bundle.
- Discovery URLs are allow-list-scoped; a hostile `resource_metadata` cannot redirect
  acquisition off-path.
- Secrets are sourced from the store ([ADR 0003](../adr/0003-credential-store-pluggable.md));
  none are inlined in manifests, logs, or tests. Tests use synthetic tokens only.

## 6. Gates (must be green before merge to `main`)

- Deterministic: `gates/checks.*` (build + test + designMap/tokens), `gates/backend-checks.*`
  (backend build/test + plan-only IaC checks). New unit tests for discovery,
  acquisition, audience guard; the P4 parity test against `mobbin-clone`.
- Semantic: the **Adversarial Judge** against `gates/rubric.md` returns PASS for the
  P3/P4 slices.
- No secret material in the diff; `git` history clean.

## 6.1 Build findings (2.0-beta, evidence-backed)

Recorded as phases are implemented so the reasoning survives the branch.

**Phase ledger (live).** The commit-phases below implement the plan's discovery +
egress-fronting mechanics + per-user acquisition; the **slim-down (P4)** is the next
unbuilt phase.

| Phase | Scope | Status | Evidence |
|---|---|---|---|
| P1 | RFC 9728/8414 discovery + classifier + `oauth-mcp` recipe shape | ✅ done | `a007c51` |
| P2a | audience/resource guard wired into egress | ✅ done | `2926b47` |
| P2b | MCP-aware egress action model (read vs manage from the JSON-RPC method/tool) | ✅ done | `3942865` + fix `0c3bf50` |
| P3 | per-user OAuth acquisition **mechanism** (PKCE + auth-code + refresh acquirer) — unit-tested, not yet wired into a live acquire/refresh flow | ✅ done (mechanism) | `727ca25` |
| P4 | slim down Tessera — retire bespoke per-target credential code the generalization replaces | ✅ analyzed — **empty by design** (nothing to retire) | `527e830` |
| W | **wire acquisition end-to-end** — connect-wizard callback (`code`→`AcquireAsync`, `state`/CSRF) + orchestrator `oauth-mcp` branch (route to `OAuthMcpAcquirer.RefreshAsync`, not `SessionRefresher`) | ⬜ not-started | — |
| C | **conformance** (plan §3 P4) — front `mobbin-clone` `/mcp` through Tessera end-to-end (`tools/list` + a tool call return inline images, client holds no token) + the 402→200 entitlement demo; **needs a minimal OAuth AS added to `mobbin-clone`** first | ⬜ not-started | — |
| — | homelab rollout (§4) | ⛔ out of scope this run (pre-rollout only) | plan-only |

> Vocabulary note: original §3 numbered the phases P1–P4 (discovery → acquisition →
> egress fronting → conformance). Implementation split "egress fronting" into the
> audience guard (P2a) + the MCP-aware action model (P2b) and sequenced them **before**
> acquisition — the egress mechanics are testable with a synthetic bearer, ahead of the
> live OAuth round-trip. The table above is the authoritative status.

- **P1 done** (`a007c51`): RFC 9728/8414 discovery + classifier + the `oauth-mcp`
  recipe shape; full suite green. Discovery's `HttpClient` **must** be SSRF-guarded
  (hard-documented contract on `OAuthMcpDiscovery`).
- **P2a done** (`2926b47`): the §4 **audience guard** (`OAuthMcpAudience.IsBound`) binds
  an `oauth-mcp` recipe's injected token to its resource and is wired into
  `EgressProxyEndpoint` after the SSRF/port checks. 11 tests; full suite 477 green.
- **P2b done** (`3942865`, review fix `0c3bf50`): the raw `/v1/egress` proxy is
  CalDAV-shaped — `MapMethodToAction` classifies **`POST` ⇒ `manage` (step-up write)**,
  but MCP uses `POST` for **reads** (`tools/list`, query tool calls), so proxying an MCP
  verbatim would force out-of-band approval on every read. Added `McpActionClassifier`
  (parse the JSON-RPC body → classify by method/tool: non-`tools/call` = read; a
  `tools/call` is a write iff the tool's **action plane** is `manage:`; an undeclared tool
  or unparseable body = write, fail-safe) and wired it into `EgressProxyEndpoint` for
  `oauth-mcp` recipes (256 KiB body buffer, rewound so the forwarder relays it verbatim);
  non-MCP proxy recipes keep the HTTP-verb map. **Review finding fixed:** the write bit is
  the tool's `EffectivePlane == Manage` (the source the PDP enforces), **not**
  `RecipeTool.StepUp` — plane and step-up are orthogonal (ADR 0019 / `ActionPlane`), so a
  `manage:` tool that omits the step-up flag can never execute on the read plane, matching
  how `BrokerProviderGateway`/`ProviderEgress` authorize from `tool.Action`. 8 unit + 6
  end-to-end tests (read forwards + injects the upstream bearer & strips the caller token;
  declared read forwards; declared write and undeclared tool step up — 409, not forwarded;
  a `manage:` tool without step-up still holds; audience guard blocks an off-resource
  replay). Full suite 491 green (Core 290, Broker 123).
- **P3 done** (`727ca25`): per-user OAuth acquisition — the last mechanic before the
  slim-down. `Pkce` (S256-only, RFC 7636; `plain` never emitted per OAuth 2.1; fresh
  verifier per request, off the front channel) + `OAuthAuthorizeUrl` (pure builder:
  OAuth 2.1 + PKCE + RFC 8707 `resource`, carrying only the S256 challenge; `resource`
  is the **same** audience the P2a egress guard enforces, so the confused-deputy gap is
  closed end to end; `state` is the caller's CSRF nonce, verified at the callback, not
  minted here) in `Tessera.Core`, and `OAuthMcpAcquirer` in `Tessera.Providers` owning
  **both** token legs as one back-channel `application/x-www-form-urlencoded` POST —
  `authorization_code` (with the PKCE verifier) and `refresh_token`. **Secretless**
  (writes the bundle via `ICredentialWriter`, never returns token bytes — ADR 0014);
  **SSRF-guarded** token endpoint checked before any request (no unguarded ctor);
  **preserves the current refresh token** when the AS omits one (RFC 6749 §6); an
  `invalid_grant` on 400/401 ⇒ **dead grant reported, never an auto-login** (single-
  writer per ADR 0026, the RM double-spend lesson). Deliberately **not** the
  `SessionRefresher` header-injection path — it shares the store + `SsrfGuard` + token
  parse, not the request shape. **Adversarial review: PASS** (no defects; unlike P2b
  there was no misclassification bug — the design is standard-grounded and fail-safe).
  Two **deliberate** scope choices, recorded so they are not mistaken for gaps: (a)
  `expires_in`/`token_type` are **not** persisted — refresh is liveness/401-driven
  (ADR 0024/0025 oracle model), not expiry-timer-driven, so an expiry clock would be
  dead weight; (b) **public-client + PKCE only** — no `client_secret` (MCP OAuth uses
  public clients + dynamic client registration); confidential-client
  (`client_secret_basic`) is out of scope until a target needs it. 16 tests; full suite
  **507 green** (Core 297, Providers 58).
- **P4 analyzed — empty by design** (`527e830`): "slim down — retire bespoke per-target
  credential code the generalization replaces" found **nothing to retire**, and inventing
  removals would violate YAGNI + capability-preservation. Evidence: (a) Tessera has **no
  per-target hardcoding** — the credential switches are on `recipe.Injection` and action
  verbs, never on a target name (no `== "mobbin"` anywhere in `src/`; the only `mobbin`
  strings in `src/` are doc-comment examples); (b) the OAuth-MCP path **reuses**
  `InjectionKind.BearerToken` (ADR 0027) rather than adding a parallel injector, so it was
  additive/consolidated from the start; (c) all `grant_type`/PKCE code is the **new**
  `OAuthMcp/*` — there was no pre-existing OAuth acquisition code it replaced; (d) RM/OLX
  harvest-and-inject + Pomerium are explicit non-goals (untouched). The generalization
  being additive is the intended outcome, not a miss.
- **Honest re-scope of the remaining pre-rollout work.** The implementation ledger
  (P1/P2a/P2b/P3) diverged from the plan's §3 numbering; two real items remain before the
  OAuth-MCP fronting is functional end to end:
  - **W (wiring).** The acquisition mechanism (`OAuthMcpDiscovery`, `OAuthAuthorizeUrl`,
    `OAuthMcpAcquirer`) is built + unit-tested but **not called by any live code** — the
    live refresh paths (`ProviderEgress`, `SessionRefreshOrchestrator`) still use
    `SessionRefresher`. There is **no callback endpoint** to turn a consent `code` into a
    stored token, and **no orchestrator branch** routing an `oauth-mcp` recipe to
    `OAuthMcpAcquirer.RefreshAsync`. Until W lands, no user can acquire a token through the
    running broker. (Egress-side enforcement — audience guard P2a + action model P2b — **is**
    wired.)
  - **C (conformance, = plan §3 P4).** No end-to-end proof that `tools/list` + a tool call
    return inline images **through Tessera** with the client holding no token, and no
    402→200 entitlement demo. **Blocker:** `mobbin-clone` is not yet a full OAuth AS — it
    answers 401 + RFC 9728 `resource_metadata` and validates a static bearer, but hosts
    **no** `authorize`/`token` endpoints, so there is nothing for W's acquirer to exchange a
    code against. C therefore requires a **minimal conformant OAuth AS** added to
    `mobbin-clone` first (authorize → code, token → code/refresh grant, PKCE S256).

## 7. Non-goals (this iteration)

- RFC 8693 token exchange from Tessera's own IdP to the upstream AS (a future
  optimization when the upstream trusts our IdP — removes the user round-trip).
- Superseding Pomerium for pure read/browse OAuth-MCPs — that remains the documented
  lighter path ([ADR 0018](../adr/0018-access-gateway-and-action-broker.md)).
- Any change to the class-2 harvest-and-inject targets (RM/OLX/…): untouched.
