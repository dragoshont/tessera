# Tessera gap analysis — design, UX & OSS portability, in the face of the session-death issues

> **Status:** assessment (2026-06-24). Input to the connection-health SDD and to
> [ADR 0025](../adr/0025-liveness-first-class-invariant.md). Grounded in Tessera
> `README`/`SECURITY`/`architecture`/`positioning`/`getting-started`, ADRs
> 0002/0015/0020/0022/0024, the .NET solution + `deploy/`, sessionkeeper
> `harvester-spec` + code, reginamaria-mcp/apple-mcp/libgsa, and two incidents:
> the 5-day RM silent death, and the reverted Phase-B cutover (`ca148ee`
> "RM session went stale").

## The one-sentence finding

**Tessera is confidentiality-complete and availability-incomplete.** Every
invariant it defends (R1–R3, the five threat-model rules) is about *not leaking
the secret* or *not being a confused deputy*. Both real incidents were the
opposite failure — the brokered session was perfectly secret and perfectly
**dead**, silently, for days. The gaps below all radiate from that blind spot.

## I. Design gaps

1. **No liveness/availability invariant.** SECURITY R1/R2/R3 and architecture §6's
   five invariants are all C/I. [ADR 0025](../adr/0025-liveness-first-class-invariant.md)
   is the first to name availability as an invariant; promote it.
2. **Detector↔re-seeder split, real but unrealized.** The harvester-spec diagrams
   `refresh dead → escalate to harvester`, but the keeper's `probe()` is an
   optimistic `ttl_hint`, so the escalation never fires. Intent and implementation
   diverged silently. (Fixed by ADR 0024 / SDD-42.)
3. **Injection↔proxy split — two credential models mid-migration.** sessionkeeper
   injects (MCP holds the session); ADR 0015's target is the proxy form (MCP holds
   nothing). This is R1 and the v0.5.2/v0.6.0 fork. The cutover is fragile for two
   reasons: the liveness loop wasn't closed, **and** token-freshness — injection
   rotates in-memory (instant), the proxy path goes KV→ESO→pod (stale). Tessera-as-
   rotator must solve the freshness in-memory rotation got for free.
4. **Worker model: designed dispatch vs deployed sidecar.** Architecture §5 + ADR
   0002 describe Tessera *dispatching* sessionkeeper via a capability-registration
   protocol; the deployed reality is sidecars with Tessera egress off for RM. The
   dispatch architecture is unbuilt — the cutover aims at a topology that doesn't
   exist yet.
5. **Observability asserted, not designed-in.** First-class **audit** exists; no
   first-class **health** model (alive / last-rotated / dies-at / consecutive-
   failures). Monitoring is bolted on from outside.
6. **Single-rotator is convention, not enforcement.** A4 holds "by construction";
   nothing at runtime prevents a second rotator. A KV lease/CAS would enforce it.

## II. UX gaps

1. **Silent failure is the cardinal sin — and it happened.** The awareness
   dashboard showed green while dead. Health must be use-based truth, and
   degradation must **push**, not wait to be noticed.
2. **reCAPTCHA hand-off is operator-grade; the product is family-grade.** Recovery
   is `kubectl port-forward` + noVNC. A non-technical family member cannot do that.
   The connect-wizard / live hand-off (ADR 0016) must be genuinely family-usable.
3. **Opaque end-user error surfacing.** A failed brokered call (dead session /
   step-up / denied) needs a structured, actionable chat surface, not a swallowed
   transport error.
4. **Operator onboarding is Azure-Entra-heavy.** Entra app + manual admin-consent +
   token capture + grants/bindings/recipes YAML. The portal softens "add a person";
   first stand-up is still heavy.
5. **"Is it healthy right now?" has no self-serve answer** without `kubectl`.

## III. OSS portability gaps

*Credit first:* above-average hygiene — MIT, pluggable stores, Diátaxis wiki,
getting-started, a GitHub Pages portal demo, examples, prior-art alignment, a
mechanical no-secrets gate.

1. **.NET 10 narrows the contributor pool** (the agent/homelab community skews
   Python/Go/Node); heavier "try it" footprint than the Python spike it replaced.
2. **Two-repo, two-language split unresolved for adopters** (.NET broker "wraps"
   a Python harvester over a protocol that's planned while the glue is sidecars).
3. **Azure/Entra gravity vs the pluggable promise** — the golden path is Azure;
   the non-Azure path is dimmer.
4. **Dependent services aren't portable, and no community path makes that OK** — no
   recipe registry/contribution path, no genuinely reusable reference MCP (a
   standard-CalDAV `apple-mcp` could be one).
5. **libgsa (Apple GrandSlam) is the least-portable dependency** (SRP-6a + anisette,
   reverse-engineered, ToS-grey) — fence it as advanced/at-your-own-risk.
6. **No adopt-vs-build self-selection** — say loudly that Vault Agent covers
   service-secret injection and Tessera's niche is the un-API'd-web + per-person
   delegation for agents/MCP.

## IV. sessionkeeper + dependent services

- **sessionkeeper** is the right-shaped OSS engine; its hole is the optimistic
  probe (the unrealized escalation) + DRAFT-spec status.
- **reginamaria-mcp / apple-mcp** are caught mid-injection↔proxy cutover; their
  fragility is inherited from the unclosed loop + freshness, not their own code.
- **libgsa** is the niche Apple crypto cold-login — powerful, fragile, least
  portable.

## V. Prioritized recommendations

| # | Lens | Move |
|---|---|---|
| 1 | Design/UX | **Connection-health SDD** — Tessera emits a truthful, use-based per-connection health verdict; the portal/awareness dashboard renders it (no optimistic green); degradation is visible at a glance. Promotes ADR 0025. *(The SDD this analysis feeds.)* |
| 2 | UX | Proactive degradation notification + a family-grade re-login hand-off + actionable chat errors |
| 3 | Design | Resolve worker-dispatch vs sidecar; add a KV lease/CAS to *enforce* single-rotator |
| 4 | Portability | Non-Azure golden path (compose + OpenBao + Authentik/Dex); clarify the Tessera↔sessionkeeper boundary; one reusable reference MCP; recipe-contribution path; explicit adopt-vs-build + ToS posture |
| 5 | Design | Solve token freshness (read-through-on-401 / short lease) before re-attempting the v0.6.0 cutover |

**Throughline:** *availability is a property you design, observe, and recover — not
one you assume.* Tessera designed secrecy beautifully and assumed liveness; the
homelab proved the assumption false twice. Recommendation #1 is the SDD that turns
the assumption into a designed, observable, verifiable property.
