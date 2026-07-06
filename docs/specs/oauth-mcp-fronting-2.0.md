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

**Phase ledger (live).** The commit-phases below implement discovery, egress-fronting,
per-user acquisition, the full acquisition WIRING (W1+W2a+W2b), a security-review pass, and
**acquisition conformance (C1)** against the real clone; **egress conformance (C2)** is the
next unbuilt slice.

| Phase | Scope | Status | Evidence |
|---|---|---|---|
| P1 | RFC 9728/8414 discovery + classifier + `oauth-mcp` recipe shape | ✅ done | `a007c51` |
| P2a | audience/resource guard wired into egress | ✅ done | `2926b47` |
| P2b | MCP-aware egress action model (read vs manage from the JSON-RPC method/tool) | ✅ done | `3942865` + fix `0c3bf50` |
| P3 | per-user OAuth acquisition **mechanism** (PKCE + auth-code + refresh acquirer) — unit-tested, not yet wired into a live acquire/refresh flow | ✅ done (mechanism) | `727ca25` |
| P4 | slim down Tessera — retire bespoke per-target credential code the generalization replaces | ✅ analyzed — **empty by design** (nothing to retire) | `527e830` |
| C0 | **`mobbin-clone` OAuth AS** — RFC 8414 metadata + authorize/token + PKCE S256 + rotating refresh; resource gate admits issued tokens (unblocks W+C) | ✅ done | clone `32c158b` |
| W1 | oauth-mcp **rotation** wired into the refresh orchestrator (acquirer stamps the refresh context in the bundle; orchestrator routes an oauth-mcp binding to `RefreshStoredAsync`) | ✅ done | `75ddbc4` |
| W2a | **connect state machine** — pending-authorization store (single-use/TTL state) + `OAuthMcpConnectService` (Begin/CompleteAsync) + discovery-orchestrating `BeginForRecipeAsync` | ✅ done | `b340271` + `3637e27` |
| W2b | **Broker host wiring** — `oauthMcp` config (client id + redirect URI), an SSRF-guarded discovery `HttpClient`, DI (pending store + acquirer + connect service; acquirer into the orchestrator), and the `POST /oauth/mcp/connect` + `GET /oauth/mcp/callback` endpoints (+ per-principal binding) | ✅ done | `f26ab77` |
| C1 | **acquisition conformance** — Tessera's real discovery + PKCE auth-code + refresh against the REAL running clone AS (opt-in in-process test) | ✅ done | `575d107` |
| C2 | **egress conformance** (plan §3 P4) — `tools/list` + a tool call return inline images THROUGH Tessera (client holds no token) + the 402→200 entitlement demo | ✅ done (deterministic); ⏳ two-family judge gate pending | working tree (2.0-beta) |
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
- **C0 done** (clone `32c158b`): the `mobbin-clone` repo now hosts a minimal conformant
  OAuth 2.1 AS — RFC 8414 metadata at `/.well-known/oauth-authorization-server`, an
  `/oauth/authorize` (public client + PKCE **S256 only**, redirect-URI allow-list so it is
  not an open redirector, RFC 8707 resource match, single-use short-TTL code) and an
  `/oauth/token` (authorization_code with exact redirect/client binding + constant-time
  PKCE verify; rotating single-use refresh). Its resource gate now admits an AS-issued
  access token as well as the static bearer, **fail-closed + backward-compatible** (empty
  allow-list issues nothing). 45 tests green (24 new). This **unblocks W and C** — there is
  now a real token endpoint for Tessera's acquirer to exchange a code/refresh against.
- **W1 done** (`75ddbc4`): oauth-mcp rotation is wired into the refresh orchestrator. The
  acquirer stamps the non-secret refresh context (token endpoint, client id, RFC 8707
  resource) into the bundle `Extra` at acquire time; `RefreshStoredAsync` rotates from that
  stored context alone (no re-discovery), still re-checked by the `SsrfGuard` (defence in
  depth). `IsTesseraOwned` now covers an oauth-mcp recipe (owner=tessera WITHOUT a
  `refreshSpec` — its refresh IS the OAuth token endpoint); `RunPassAsync` routes it to the
  acquirer and a header-injection recipe to `SessionRefresher`, both under the single-writer
  lease. +7 tests.
- **W2a done** (`b340271`): the per-user connect state machine. A single-use, TTL'd
  pending-authorization store (state = a 256-bit CSRF binding) + `OAuthMcpConnectService`:
  `Begin` mints PKCE+state, stashes the exchange (verifier stays server-side), and builds the
  authorize URL; `CompleteAsync` redeems the code via the acquirer (an unknown/expired/replayed
  state or a code-less callback is refused WITHOUT hitting the AS); `BeginForRecipeAsync` adds
  the RFC 9728/8414 discovery step (over an SSRF-guarded client) → authorize URL. +12 tests
  (Providers 77).
- **W2b done** (`f26ab77`): the connect flow is composed into the running Broker, so a
  per-user OAuth-MCP session can now be acquired end to end. An `oauthMcp` config block
  (enabled + the registered redirect URI + client id, fail-closed validation) gates two
  endpoints: `POST /oauth/mcp/connect` (operator-authenticated — refuses a non-oauth target
  or a non-admin cross-principal connect, mints a deterministic per-(target,principal) secret
  name, discovers the AS over the guarded client, returns the authorize URL + state) and the
  **public** `GET /oauth/mcp/callback` (the 256-bit single-use `state` is the CSRF capability;
  an unknown/expired/forged state is refused with no token call and no binding; a real
  acquisition creates the per-principal binding, the token itself written by the acquirer,
  never surfaced). BrokerHost builds ONE acquirer shared by the endpoints and the W1 rotation
  owner, reusing the data-egress SSRF allow-list + address guard (discovery via a new
  `HttpClientTransport.CreateGuardedHttpClient`). **Adversarial review: PASS.** Also fixed a
  latent `ConfigLoader` bug — `ApplyEnvironmentOverrides` rebuilt the config with a partial
  field list that silently dropped `LiveView`/`Refresh`/`Freshness` (and would have dropped
  `OAuthMcp`) back to defaults after load. +8 Broker integration tests (disabled=404,
  unauthenticated=401, non-oauth=400, cross-principal=403, bad-callback=400, and the full
  begin→callback→binding path against a stub AS). Full suite **534 green**. **W is complete
  end to end; C (conformance) is next.**
- **Security review pass** (`ac092d4`): a fresh adversarial audit of P1..W (5 lenses). No
  CRITICAL. Three MINOR (defense-in-depth) fixed + tested: D1 (discovery + the browser
  authorize-redirect are now host-allow-list guarded, not just IP-guarded — a hostile upstream
  can't steer them off-list); AC2 (the MCP action classifier detects `tools/call`
  case-insensitively, so a lenient upstream can't slip a mutating call past as a read); CFG1
  (`oauthMcp.redirectUri` must be https unless loopback, RFC 8252). NITs recorded.
- **C1 done** (`575d107`): acquisition conformance against the REAL clone. An opt-in in-process
  test (`TESSERA_CONFORMANCE=1`) spawns the `mobbin-clone` (AS enabled) and runs Tessera's REAL
  discovery + connect + acquirer + refresh against it — no fakes on the OAuth path. It
  **immediately caught a CRITICAL bug the fake-transport unit tests missed** (RC-15
  green-but-dead): `HttpClientTransport` hard-coded `Content-Type: application/json` for every
  body, so the form-urlencoded OAuth token exchange was unparseable by the AS (400) —
  acquisition would NEVER have worked against a real AS. Fixed (honor the caller's content-type)
  + a hermetic CI regression guard. Now green end to end: RFC 9728/8414 discovery, the auth-code
  + PKCE round trip, the per-principal bundle write, and a rotating refresh — all against the
  clone's real endpoints.
- **C2 done** (deterministic gate green; two-family judge gate pending — see *Gate status* below):
  egress conformance. A new opt-in in-process test (`TESSERA_CONFORMANCE=1`,
  `Tessera_fronts_the_clone_mcp_end_to_end_and_the_client_never_holds_a_token`) spawns the clone
  with its entitlement gate live (`MCP_EXPECTED_TOKEN` + `MCP_FREE_TIER_TOKEN`) and drives a REAL
  `ModelContextProtocol` client — holding ONLY its caller token — through Tessera's `/v1/egress`:
  `tools/list` returns the real three-tool surface, `search_screens` returns inline images +
  `mobbin_url` metadata, and a free→entitled **402→200** swap (client headers constant, only the
  SERVER-SIDE stored token changes) proves the upstream credential lives server-side. Like C1, the
  REAL forward **caught green-but-dead**: the `/v1/egress` proxy was built for CalDAV/public-SaaS,
  so it hard-blocked any non-default upstream port (`!upstream.IsDefaultPort → 403`) — which would
  block both a loopback clone AND the in-cluster `svc:8080` target. Fixed at the CAUSE — the
  default-port heuristic is **skipped for `oauth-mcp` recipes** (`EgressProxyEndpoint.cs`), because
  the ADR-0027 §4 audience guard (`OAuthMcpAudience.IsBound`) already pins the exact
  scheme+host+**port**+path to the recipe's resource (strictly stronger than "default port only");
  the CalDAV/proxy path is unchanged (still default-port-only, still `AddressGuard.PublicOnly`). A
  hermetic regression test (`Mcp_oauth_recipe_on_a_non_default_port_is_forwarded`) guards it in
  normal CI. Loopback reach for the conformance test uses a test-only
  `BrokerHostOptions.AddressGuardOverride` (mirrors the `ForwarderOverride`/`StoreOverride` seams;
  production stays `PublicOnly`). Full suite **543 green**; the 2 conformance tests pass with the flag.
  - **Known follow-up (§4 rollout, NOT this slice):** the proxy egress still uses
    `AddressGuard.PublicOnly`, which blocks a **private** in-cluster ClusterIP. An in-cluster
    `oauth-mcp` upstream would need the proxy egress to use `AddressGuard.Default` for `oauth-mcp`
    recipes (private reachable; loopback/link-local/metadata still refused). Deferred with the rest
    of §4 and documented here so the rollout is not attempted assuming C2 made an in-cluster private
    target reachable. The port-gate fix above is necessary-but-not-sufficient for that target.
  - **Gate status:** deterministic (build + 543 hermetic + the 2 conformance tests) is GREEN. The
    plan §6 **semantic Adversarial-Judge gate could not be executed in this environment** (the judge
    sub-agent runtime failed to spawn); a rigorous adversarial self-review found no defects, but the
    independent two-family judge + human review remain required before merge to `main`.

## 7. Non-goals (this iteration)

- RFC 8693 token exchange from Tessera's own IdP to the upstream AS (a future
  optimization when the upstream trusts our IdP — removes the user round-trip).
- Superseding Pomerium for pure read/browse OAuth-MCPs — that remains the documented
  lighter path ([ADR 0018](../adr/0018-access-gateway-and-action-broker.md)).
- Any change to the class-2 harvest-and-inject targets (RM/OLX/…): untouched.
