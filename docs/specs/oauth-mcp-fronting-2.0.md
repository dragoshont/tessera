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

Recorded as phases are implemented so the reasoning survives the branch:

- **P1 done** (`a007c51`): RFC 9728/8414 discovery + classifier + the `oauth-mcp`
  recipe shape; full suite green. Discovery's `HttpClient` **must** be SSRF-guarded
  (hard-documented contract on `OAuthMcpDiscovery`).
- **P2a done** (`2926b47`): the §4 **audience guard** (`OAuthMcpAudience.IsBound`) binds
  an `oauth-mcp` recipe's injected token to its resource and is wired into
  `EgressProxyEndpoint` after the SSRF/port checks. 11 tests; full suite 477 green.
- **P2b — MCP-aware egress (NEW; blocks the end-to-end "front the clone").** The raw
  `/v1/egress` proxy is CalDAV-shaped: `MapMethodToAction` classifies **`POST` ⇒
  `manage` (step-up write)**. MCP uses `POST` for **reads** (`tools/list`, query tool
  calls), so proxying an MCP verbatim through it would force out-of-band approval on
  every read. Fronting MCP correctly needs an **MCP-aware action model** — classify the
  JSON-RPC method/tool (`tools/call` to a query tool = read; only a mutating tool =
  write), reusing the recipe tool→action map (`RecipeTool.Action`/`StepUp`, ADR 0014)
  instead of the HTTP-verb map, with request-body buffering. This is design-then-
  implement, not a quick slice — it is the real "front the MCP" work.

## 7. Non-goals (this iteration)

- RFC 8693 token exchange from Tessera's own IdP to the upstream AS (a future
  optimization when the upstream trusts our IdP — removes the user round-trip).
- Superseding Pomerium for pure read/browse OAuth-MCPs — that remains the documented
  lighter path ([ADR 0018](../adr/0018-access-gateway-and-action-broker.md)).
- Any change to the class-2 harvest-and-inject targets (RM/OLX/…): untouched.
