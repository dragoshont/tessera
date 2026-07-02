# ADR 0027 — Broker fronts upstream OAuth-MCP servers (per-user token bridge)

- **Status:** Proposed (2.0-beta)
- **Deciders:** maintainer (Dragoș)
- **Relates to:** [ADR 0009](0009-end-user-identity-propagation.md) (per-user delegation),
  [ADR 0014](0014-http-injectable-provider-egress.md) (injectable egress + single owner),
  [ADR 0015](0015-mcp-egress-through-tessera.md) (domain-MCP egress through Tessera),
  [ADR 0018](0018-access-gateway-and-action-broker.md) (Pomerium out front; Tessera the action broker),
  [ADR 0020](0020-credential-ownership.md) (user vs service vs dependent),
  [ADR 0026](0026-single-writer-rotation-lease-and-fencing.md) (single-writer rotation)

## Context

Tessera injects a credential **it already holds** — a bearer, cookie set, API key,
or Basic pair ([ADR 0014](0014-http-injectable-provider-egress.md),
`InjectionKind`) — into an allow-listed HTTP or MCP upstream by verified principal.
Every target we have modelled so far hands its credential over as one of those
static shapes, harvested by a driver ([ADR 0006](0006-harvest-drivers.md)) and kept
warm.

A distinct class of target is now common enough to model as a first-class citizen:
services that **ship their own OAuth-MCP**. Concretely — `https://api.mobbin.com/mcp`,
and the private corporate MCP an enterprise wants us to broker — expose a
Model Context Protocol endpoint whose auth is **OAuth 2.1 with RFC 9728 protected-
resource metadata**. An unauthenticated probe answers:

```
POST /mcp → 401 Unauthorized
www-authenticate: Bearer resource_metadata="https://…/.well-known/oauth-protected-resource"
```

That single response is a **decidable classifier**. A target either:

1. **ships its own OAuth-MCP** (401 + `WWW-Authenticate: Bearer resource_metadata=…`,
   RFC 9728) — the required credential is a **per-user OAuth bearer minted by the
   upstream's *own* authorization server**; or
2. **does not** (Regina Maria, OLX, a camping-permit site) — there is no upstream
   authorization server to mint against; the credential is a **harvested session**
   (cookie/token) that we inject (the model we already have).

Today Tessera can *inject* a bearer (`InjectionKind.BearerToken`) but has **no path
to acquire or keep a per-user upstream OAuth token**, and **no recipe shape** that
declares "this target is an OAuth-MCP — discover its authorization server and bridge
the token per user." So an OAuth-MCP target is currently un-modellable without
bespoke, credential-holding, per-target code — exactly the two-parallel-credential-
planes anti-pattern [ADR 0015](0015-mcp-egress-through-tessera.md) removed.

[ADR 0018](0018-access-gateway-and-action-broker.md) already placed **Pomerium** at
the browser-access edge and kept Tessera as the hidden-credential / action broker.
This ADR is the **credential-plane complement**: it lets Tessera be the single
custodian for OAuth-MCP upstreams where a *per-user brokered token* is needed for
the action plane (JIT, step-up, confirm-gated writes, audit) — not merely a browser
session.

## Decision

**Add a first-class recipe shape and acquisition path for OAuth-MCP upstreams. The
novelty is discovery + per-user token acquisition; injection and egress are reused
unchanged.**

1. **Recipe shape.** A recipe may declare its upstream is an `oauth-mcp` (the MCP
   URL). Discovery — not the operator — finds the authorization server: fetch the
   RFC 9728 `oauth-protected-resource` document named by the `WWW-Authenticate`
   challenge, then the AS metadata (RFC 8414). The recipe declares scopes and the
   resource; it does **not** hardcode endpoints.

2. **Acquisition driver.** A new harvest/acquire driver ([ADR 0006](0006-harvest-drivers.md))
   performs OAuth 2.1 **authorization-code + PKCE** once per user (the user round-trip
   is hosted by the connect wizard, [ADR 0016](0016-admin-portal.md)), then **refresh**
   thereafter. The obtained access/refresh tokens are stored in the principal's bundle
   exactly like any other credential ([ADR 0003](0003-credential-store-pluggable.md),
   [ADR 0020](0020-credential-ownership.md) → **user-owned delegation**). Refresh-token
   rotation is single-writer ([ADR 0026](0026-single-writer-rotation-lease-and-fencing.md)).

3. **Injection + egress reused.** Once the bundle holds an access token, the existing
   `InjectionKind.BearerToken` injects it and the existing MCP egress
   ([ADR 0015](0015-mcp-egress-through-tessera.md)) fronts the upstream MCP's
   streamable-HTTP JSON-RPC. **No new injection primitive.** The upstream token is
   resolved **per principal** ([ADR 0009](0009-end-user-identity-propagation.md)); the
   client never holds it.

4. **Audience/resource guard.** An acquired upstream token is bound to its resource
   (the `resource` from RFC 9728). Egress refuses to inject a token into any host but
   its bound resource — a token-confused-deputy / SSRF guard layered on the existing
   allow-list ([ADR 0014](0014-http-injectable-provider-egress.md) §SSRF).

5. **Cookie-harvest stays the fallback.** Class-2 targets (no OAuth-MCP) keep the
   harvest-and-inject model unchanged (`Cookies` / `ApiKeyHeader` / `Basic`).

**Conformance target.** The 2.0 work is built and tested against
**`mobbin-clone-mcp`** — a separate, self-hosted MCP that reproduces a real OAuth-MCP
(the exact Mobbin tool surface: `search_flows` / `search_screens` / `search_sections`,
byte-identical schemas, inline images, `401 + WWW-Authenticate` RFC 9728, and an
optional `402` free-tier gate) with **synthetic data**. It lets us implement and
verify the full acquire→store→inject→front→refresh loop **without a paid Mobbin plan
or access to the private corporate MCP**, and it is the deterministic fixture for the
egress/refresh tests.

## Consequences

**Positive.**
- OAuth-MCP targets (Mobbin, the corp MCP, any RFC-9728 MCP) become **recipes, not
  bespoke credential-holding code** — the [ADR 0015](0015-mcp-egress-through-tessera.md)
  single-custodian invariant now covers this class too.
- The client/model only ever talks to Tessera; the upstream only ever sees a valid,
  per-user, audience-bound bearer. Per-user JIT / step-up / audit / confirm-gated
  writes ([ADR 0018](0018-access-gateway-and-action-broker.md), [ADR 0023](0023-phase3-write-confirmation-out-of-band.md))
  apply to OAuth-MCPs uniformly.
- Reuses injection, egress, SSRF, audit, and rotation — the surface added is
  discovery + acquisition, not a second broker.

**Negative / cost (stated honestly).**
- Tessera must now run an **OAuth 2.1 client** (auth-code + PKCE + refresh) and store
  **per-user upstream refresh tokens** — new rotating secret material, gated by the
  single-writer lease ([ADR 0026](0026-single-writer-rotation-lease-and-fencing.md)) to
  avoid the double-refresh hazard.
- The first acquisition needs a **user round-trip** (authorization-code redirect
  capture); it cannot be fully headless. The connect wizard ([ADR 0016](0016-admin-portal.md))
  hosts it. This is inherent to OAuth-MCPs and is the honest cost of not holding the
  user's IdP password.
- Discovery adds a network dependency and a small attack surface (a malicious
  `resource_metadata` URL). Discovery is allow-list-scoped and the resource binding
  (§4) contains the blast radius.

**Relationship to prior ADRs.** Complements — does **not** supersede —
[ADR 0018](0018-access-gateway-and-action-broker.md) (Pomerium remains the browser-
access plane and a valid *lighter* path for pure read/browse OAuth-MCPs) and extends
[ADR 0014](0014-http-injectable-provider-egress.md) / [ADR 0015](0015-mcp-egress-through-tessera.md)
(same custodian, one more acquisition shape).

## Rejected alternatives

- **A — Let Pomerium bridge the upstream OAuth for MCPs and stop there.** Pomerium's
  MCP gateway does RFC 9728 upstream-OAuth and injects the upstream token so clients
  never see it — genuinely the right tool for *pure read/browse* OAuth-MCPs, and we
  keep it as the documented lighter option ([ADR 0018](0018-access-gateway-and-action-broker.md)).
  Rejected as the **sole** answer because the action plane needs per-user brokered
  tokens with JIT / step-up / confirm-gated writes / unified audit *and* the harvest-
  fallback targets in **one** custodian; splitting OAuth-MCPs into Pomerium and
  everything else into Tessera reintroduces two credential planes for the same user.
- **B — A bespoke `mobbin-mcp` / `corp-mcp`, each holding its own OAuth.** Rejected —
  the exact duplicated-credential, split-audit, rotation-contention anti-pattern
  [ADR 0015](0015-mcp-egress-through-tessera.md) exists to remove.
- **C — Cookie-harvest everything with Steel.** Rejected — an OAuth-MCP's bearer is
  minted by *its* authorization server; harvesting a browser session cookie does not
  yield that bearer. Right tool for class-2 targets, wrong tool for class-1.
