# ADR 0015 — Domain MCPs egress through Tessera (the credential-proxy target)

- **Status:** Proposed (the architecture is decided and **built** — caller plane
  ADR 0021, provider egress ADR 0014, a credential-free domain-MCP client; the
  operational **cutover to live providers** is the remaining operator step)
- **Deciders:** maintainer (Dragoș)
- **Relates to:** [ADR 0003](0003-credential-store-pluggable.md) (store),
  [ADR 0007](0007-worker-transport.md) (broker⇄worker transport),
  [ADR 0009](0009-end-user-identity-propagation.md) (per-user delegation),
  [ADR 0014](0014-http-injectable-provider-egress.md) (provider egress + single owner)

## Context

[ADR 0014](0014-http-injectable-provider-egress.md) gave Tessera the ability to
**inject a credential by verified identity and perform the upstream call**. In
In practice, though, a real deployment also runs **domain MCP servers** — e.g. a
`portal-mcp` — that wrap a provider's API as *nice, model-friendly tools*
(resolve a specialty name → id, rank previously-seen doctors, normalize the
appointment payload into a clean shape, gate booking behind a per-call confirm).

Today those domain MCPs call the upstream **directly**, holding the provider
credential themselves (read from the vault, refreshed in-process). That creates
**two parallel credential planes to the same upstream**:

| Plane | Holds the credential? | Calls upstream? | Identity? |
|---|---|---|---|
| Domain MCP (`portal-mcp`) | **yes** (own vault read + rotation) | **yes**, directly | per-deployment (one account) |
| Tessera ([ADR 0014]) | yes (vault read + inject) | yes, when egress on | per verified principal |

The credential is duplicated; the domain MCP and Tessera each refresh the same
single-use chain (a rotation-contention hazard, [ADR 0014] §2); and audit/SSRF
controls are split. The operator's stated goal is the opposite: **one credential
custodian**, with domain MCPs keeping their valuable tool ergonomics but holding
**no secret of their own**.

We considered three shapes:

- **A — Drop the domain MCP.** The chat calls Tessera's generic `tessera_call`
  directly. *Rejected:* loses the domain MCP's name-resolution, ranking, output
  normalization, and the booking confirm-UX — the model would have to drive a raw
  HTTP surface.
- **B — Thin pass-through MCP.** The domain MCP forwards every call to Tessera with
  no logic. *Rejected for the same reason:* it throws away the domain ergonomics.
- **C — Domain MCP keeps its tools, but egresses *through* Tessera.** ✅ Chosen.

## Decision

**A domain MCP keeps its domain tools and output shaping, but holds no upstream
credential. Its HTTP egress is routed through Tessera, which is the single
credential custodian: it authorizes, resolves the bundle by principal, injects the
credential, enforces the SSRF allow-list, owns rotation, and audits.**

```
        ┌──────────────┐  (target, tool, args, principal)   ┌───────────────────┐
 chat → │ domain MCP   │ ─────────────────────────────────▶ │ Tessera egress    │
        │ (tools +     │                                     │  authorize        │
        │  shaping;    │ ◀───────────── result ───────────── │  resolve bundle   │
        │  NO secret)  │                                     │  inject + SSRF    │──▶ upstream
        └──────────────┘                                     │  audit + rotate   │     API
                                                             └───────────────────┘
```

### 1. The domain MCP becomes credential-free

It no longer reads `TokenSSO`/`RefreshTokenSSO`/API keys from the vault. It keeps:
its tool surface, input schemas, **output normalization** (the model-friendly
shapes), and the per-call **confirm** gate for writes. It gains a Tessera client
(an `IProviderEgress`-shaped HTTP call) and loses `keepwarm.py` / direct rotation
(rotation moves to Tessera, see §3).

### 2. Identity propagation — two supported modes

The egress must run **as a principal** so Tessera resolves the right account.

- **Mode P (per-account deployment) — start here.** Mirrors today's model: one
  domain-MCP deployment per person. The deployment carries a **service identity**
  (how it authenticates *to Tessera*) plus a fixed **act-as principal** (which
  account it operates). `portal-mcp` → `alice@example.com`, `portal-mcp-bob` →
  `bob@example.com`. Simplest; no inbound user token needed; isolation is physical.
- **Mode U (multi-user) — later.** A single domain-MCP deployment **forwards the
  end-user's OIDC token** to Tessera, which resolves the principal from it (the
  [ADR 0009] path). Requires the MCP to sit on the chat token path and a per-call
  gate. Use when one shared deployment must serve many users.

The **act-as principal a service caller may assert is itself authorized by a
grant** (a service caller may only act-as the principals its grant lists) — so a
compromised domain MCP cannot impersonate an arbitrary user.

### 3. Tessera becomes the rotation owner (Phase B)

With the domain MCP no longer refreshing, the single-session-owner invariant
([ADR 0014] §2) is satisfied by **Tessera** owning rotation: the `SessionRefresher`
(built but unwired in [ADR 0014]) is enabled, with the harvester still owning only
cold seed / re-seed. This removes the rotation-contention hazard by construction.

### 4. The MCP → Tessera caller plane

The domain MCP authenticates to Tessera as a **non-human caller** ([ADR 0009]
"automation caller"). Phased:

1. **NetworkPolicy + a service OIDC token** (a workload identity / client-credential
   token whose `aud` is Tessera), validated like any caller; START here.
2. **mTLS** over the typed broker plane ([ADR 0007]) as the hardened end state.

The inbound end-user token (Mode U) is **never passed to the upstream** — Tessera
injects the provider cookie/bearer instead (MCP no-token-passthrough, security
spec). The upstream sees only the injected provider credential.

## Consequences

**Positive**

- **One credential custodian.** The provider secret lives only in the vault and is
  injected only by Tessera. Domain MCPs hold nothing; a compromised domain MCP
  yields no standing credential.
- **Rotation contention eliminated** (Tessera is the sole refresher).
- **Centralized audit + SSRF + step-up** for every provider call, regardless of
  which domain MCP initiated it.
- **Output ergonomics preserved** — the domain MCP still shapes results for the
  model (the reason the chat reads RM appointments reliably).
- Fixes the duplication the audit flagged (two planes to one upstream).

**Negative / cost**

- A new **caller auth plane** (service token, later mTLS) the broker must validate.
- A **deliberate, identity-planed cutover** per provider — not a flip of a config
  flag. For a **medical** provider the cutover is done with the operator present
  (consistent with the project's medical-path caution).
- The domain MCP gains a network hop (MCP → Tessera → upstream) and a hard
  dependency on Tessera being up.

## Status & sequencing (important)

This is the **target**. It is **not** live. The half-wired precursor — Tessera
egress enabled against the live medical API with booking, but with no consumer —
was **removed** (the operator's private deployment, 2026-06-14, audit finding F1)
precisely because a dormant, booking-capable egress path to a live medical API is
a liability. Re-enabling Tessera egress **must** come with: the caller auth plane
(§4), a chosen identity mode (§2), the domain MCP rewritten credential-free (§1),
and — for medical providers — an operator-present cutover.

Until then, domain MCPs call their upstreams directly and own their own keep-warm,
and Tessera runs with egress **disabled** (proving only the read-only resolve spine
via its self-test).
