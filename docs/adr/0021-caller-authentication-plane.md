# ADR 0021 — Caller authentication plane (non-human callers)

- **Status:** Accepted
- **Date:** 2026-06-15
- **Supersedes / relates to:** [ADR 0005](0005-identity-first-fail-closed.md)
  (verified-identity-first), [ADR 0007](0007-worker-transport.md) (mTLS for the
  worker hop), [ADR 0009](0009-end-user-identity-propagation.md) (per-call end-user
  delegation), [ADR 0014](0014-http-injectable-provider-egress.md) (provider egress),
  [ADR 0015](0015-mcp-egress-through-tessera.md) §4 (the caller plane it names but
  did not build), [ADR 0018](0018-access-gateway-and-action-broker.md) (gateway vs
  broker split).

## Context

Tessera has two ways in. The native MCP surface at `/mcp` resolves a **forwarded
end-user token** and dispatches the `tessera_*` tools; the network brokering
endpoint at `/v1/broker` is the door for **non-human callers** — a domain MCP (the
media broker), a CLI, an n8n workflow.

`/v1/broker` is **fail-closed (503)**. The host comment is explicit:

> *"broker endpoint is fail-closed: no caller authenticator configured (mTLS/SVID
> auth plane not enabled). Use the MCP surface at /mcp."*

So today the *only* authenticated path is `/mcp`, and its caller identity is
**hardcoded** to a single value — `TesseraMcpOptions.ChatCallerId` with
`VerificationMethod.Network` (the chat→Tessera hop is NetworkPolicy-gated and a
valid end-user token rides along; review C2 / ADR 0005). That is correct *for the
chat consumer*, but it means:

1. There is **no verified caller identity for any non-chat workload.** A domain MCP
   cannot present "I am the media broker" as a distinct, verifiable `CallerIdentity`.
2. Therefore **per-caller grant scoping is impossible** beyond the one chat caller.
   You cannot write `caller: media-mcp may use:* on sonarr` and have it mean
   anything, because every non-chat caller is either rejected (`/v1/broker` 503) or
   collapsed into the single `ChatCallerId` (`/mcp`).

This is the blocker for **ADR 0015's whole cutover.** ADR 0015 decides that domain
MCPs become credential-free and egress *through* Tessera; §4 names the missing piece
as the **caller plane** and sequences it: *phase 1 = a service OIDC token (aud =
Tessera), phase 2 = mTLS.* The plane was specified and deliberately left unbuilt
(iteration 1 shipped the chat path only). A credential-free domain MCP has nothing
to authenticate *with* until this plane exists.

Everything *downstream* of the caller already exists and is tested (286 .NET tests):
the PDP (caller-must-be-verified, default-deny, planes, step-up), the
`CredentialResolver`, the `ProviderEgress` (SSRF-guarded, result-class enforced),
recipes/grants/bindings, the `SessionRefresher`, and the `IProviderGateway` the MCP
surface already calls. The app-only token path is also already built:
`EntraTokenValidator` validates an app-only (client-credentials) token and
`TesseraTokenResult.ToCallerIdentity()` maps `appid → CallerIdentity(OidcJwt)` — a
**verified** caller (`OidcJwt.IsVerified() == true`). The missing piece is narrow: a
network endpoint that authenticates the caller and dispatches into that existing spine.

## Decision

Build the **caller authentication plane** as the authenticated ingress for non-human
callers at `POST /v1/broker`, in two phases that share one endpoint and one
downstream spine.

### Phase 1 — service OIDC token (build now)

- The caller presents **its own app-only bearer token** (Entra client-credentials,
  `aud` = Tessera's configured audience) in `Authorization: Bearer …`.
- Tessera validates it with the **same `ITokenValidator`** already used for the MCP
  delegation path. The token **must be app-only** (`IsAppOnly`); a user token on the
  caller header is rejected (a human is not a caller — they are a *subject*).
- `appid → CallerIdentity(appid, VerificationMethod.OidcJwt, tenantId)` — a verified
  caller. No new crypto, no new validator, no enum change.
- The **end-user** (FOR WHOM), when present, follows the two delegation modes from
  ADR 0015 §2:
  - **Mode U (multi-user):** a *second* forwarded end-user token in a dedicated
    header (`X-Tessera-On-Behalf-Of`), validated the same way and mapped to a
    verified `EndUserAssertion`. The PDP requires it to be verified.
  - **Mode P (per-account):** no end-user; the caller acts under a fixed service
    grant. A grant-bound `actAs` (a service caller asserting a *named principal*
    without a token) is **deferred**: the PDP requires a present end-user to be
    independently verified, so an unverified asserted principal is denied by
    construction (ADR 0015 §4 anticipates it; building it needs a new verified-by-grant
    mechanism nothing yet uses).
- Dispatch reuses the existing spine **unchanged**: every `call` runs through
  `BrokerCore.HandleAsync` (authorize + resolve + **audit**) and then, on allow,
  through `IProviderGateway.CallAsync(caller, onBehalfOf, target, tool, args,
  confirmed)`; `list-tools` / `check` are read-only. The PDP, resolver, egress, audit,
  and result-class enforcement are all already in that path.

### Phase 2 — mTLS (the hardening; design now, build behind the same door)

- A client certificate terminated at the ingress / Kestrel; the validated subject
  (SAN) maps to a `CallerIdentity(VerificationMethod.Mtls, …)` — the value is
  **already in the enum**. This is ADR 0007's transport decision applied to the
  caller hop and ADR 0015 §4 phase 2.
- It slots in **behind the same `/v1/broker`** and the same downstream spine: the
  PDP/egress/recipes do not change — only *how the `CallerIdentity` is established*
  changes. No churn to the authorization or egress code.

### Two independent fail-closed gates (defense in depth)

The endpoint stays fail-closed unless **both** are satisfied — so enabling one never
opens a path on its own:

1. **A caller authenticator is configured.** With `identity.mode != oidc` (or no
   audience) the validator is `DenyAllTokenValidator`; every caller token is
   rejected and `/v1/broker` answers 503/deny. (Unchanged fail-closed default.)
2. **`egress.enabled`.** The gateway is `DisabledProviderGateway` until egress is on,
   so an authenticated caller still reaches **no upstream** until the operator opens
   egress. Authenticating the caller and opening egress are two separate, auditable
   switches.

## Alternatives considered

- **A. Keep `/v1/broker` fail-closed; route every caller through `/mcp`.** Rejected.
  `/mcp`'s caller is hardcoded to one `ChatCallerId`; it cannot represent a distinct
  automation workload, so grants can't be scoped per caller, and it conflates
  "human-in-chat" with "automation workload" (two different `VerificationMethod`s and
  two different trust stories).

- **B. A pre-shared static caller token (a bearer secret per caller, hashed in
  config) as the primary plane.** Rejected. A long-lived shared secret is exactly the
  custody problem Tessera exists to *remove*: no rotation, no issuer, no tenant
  binding, and it would itself need a custodian. Permitted only as a **dev-loopback**
  convenience (off the network, alongside `VerificationMethod.Dev`), never as the
  network plane.

- **C. mTLS-first (skip the OIDC phase).** Rejected as phase 1. The homelab has no
  SPIRE; standing up workload certs + Kestrel client-cert + SAN→caller mapping is a
  larger lift than reusing the app-only token path that **already exists and is
  tested**. mTLS is the phase-2 hardening, not the phase-1 blocker-clearer — and the
  two phases share the endpoint and spine, so phase 1 is not throwaway.

- **D. Let the domain MCP keep its credentials (no cutover).** Rejected — that is the
  status quo ADR 0015 already decided against (the domain MCP becomes credential-free;
  Tessera is the single custodian). This ADR builds the missing piece that lets 0015
  actually happen.

## Consequences

- A domain MCP / CLI / workflow authenticates as a **distinct verified caller**;
  grants scope per caller (`caller: media-mcp may use:* on sonarr` now *means*
  something and is enforced by the existing PDP).
- The **ADR 0015 cutover is unblocked.** A credential-free domain MCP has a thing to
  authenticate with; the homelab media tools can drop their keys (see the
  [caller-plane & MCP cutover spec](../specs/caller-plane-and-mcp-cutover.md)).
- The plane is fail-closed by construction (caller-authenticator gate **and**
  `egress.enabled` gate, independent). Deploying the endpoint opens nothing.
- `/mcp`'s single-chat-consumer model is **unchanged** — this ADR adds the
  *automation* caller surface *alongside* it (the human-delegation surface stays as
  is). The chat caller remains `VerificationMethod.Network`; automation callers are
  `OidcJwt` (phase 1) / `Mtls` (phase 2).
- **Out of scope (explicitly):** SSH-backed platform tools (the homelab `kube_*` /
  `host_*` / `flux_*` family) are a *different credential class* (a private key for
  arbitrary shell), which is an explicit Tessera non-goal — they keep their own
  credential and are **not** brokered through this plane. See the cutover spec §3 for
  the use-case boundary.
