# Adversarial analysis — P2 serving plane & a patient-portal integration (case study)

> Red-team review of what was actually built and deployed in this slice, not the
> ideal end-state. The goal is to be honest about what is safe *today*, what is
> deliberately fail-closed, and what must land before the network broker endpoint
> may be opened. Threats are mapped to the
> [OWASP NHI Top 10 (2025)](https://owasp.org/www-project-non-human-identities-top-10/)
> and the [MCP authorization spec](https://modelcontextprotocol.io/specification/2025-06-18/basic/authorization).

## What this slice actually does

- Resolves a credential **bundle** for `(target, on_behalf_of)` from Azure Key
  Vault, **read-only**, reusing the exact bundle the harvester/MCPs already
  maintain (`health-portal-session` = the household member's session).
- Runs the fail-closed Policy Decision Point and writes a **secret-free** audit
  line per decision.
- Proves the spine with a **startup self-test** that resolves the session
  status (`present`/`absent`) — making **no call to the patient portal** and triggering
  **no login**.
- Exposes `/healthz`, `/readyz`, `/status`, and a `POST /v1/broker` endpoint that
  is **fail-closed** (503) because no caller-authentication plane is wired yet.
- Deploys cluster-internal only: **ClusterIP + NetworkPolicy, no Ingress, not on
  the public edge.**

## The deliberate safety lines (and why)

| Decision | Rationale |
|---|---|
| **No login / no re-seed of the account** | A fresh interactive login on a real medical account risks lockout/bot-detection; that action is held for explicit human consent. Tessera is a *read-only consumer* of the already-warm session. |
| **`/v1/broker` fails closed** | Opening an unauthenticated credential-injection endpoint *is* the confused-deputy vulnerability (NHI + MCP §3.6). Until callers present verifiable identity, the endpoint must refuse. |
| **No Ingress** | A credential broker holding medical sessions must never be on the public edge. Internal consumers reach it via ClusterIP; nothing routes from Cloudflare. |
| **No upstream call in the self-test** | Proving "we can resolve the credential" needs only a KV read + structural check. Calling the portal would be an unnecessary, side-effectful touch of a medical account. |
| **Audit is secret-free by construction** | The broker hands the audit sink only identifiers + enums; the bundle never reaches it. Tested (`SUPERSECRET` never appears in the line). |

## Findings

### F1 — Confused deputy at the (future) network door — *mitigated by fail-closed*
**Threat (NHI, MCP §3.6):** a poisoned tool / compromised n8n / network attacker
calls `/v1/broker` claiming `on_behalf_of: <someone>` and receives brokered access.
**Today:** the endpoint returns 503 (no authenticator). Even if dev header-auth
were force-enabled, the PDP independently denies unverified callers — a **two-layer**
fail-closed. **Before opening it:** require mTLS/SPIFFE X.509-SVID for the caller
and a signed OIDC assertion for the end-user (validate `aud`/`exp`/`jti`/issuer).

### F2 — Over-trusting `on_behalf_of` in the request body — *contained, must harden*
**Threat:** in the current `/v1/broker` shape the end-user subject rides in the
JSON body on the (would-be) authenticated caller channel. A caller authorized for
itself could claim to act for an arbitrary human. **Today:** moot (endpoint
closed). **Before opening it:** the end-user identity must be a **separately signed
assertion** (RFC 8693 `subject_token`), not a body field; the PDP already requires
the assertion to be verified, so wiring real verification closes this.

### F3 — Blast radius: one SP can read every session — *partially mitigated*
**Threat (NHI5/NHI9):** Tessera holds the vault Service-Principal credentials,
which can read all session secrets in the vault, so a Tessera compromise exposes
every family session. **Today:** mitigated by read-only use, no inbound network
brokering, internal-only exposure, secret-free logs, and JIT reads (nothing cached
to disk; read-only root FS). **Future:** scope the SP/Workload-Identity to only the
secrets Tessera needs; separate **trust domains** for medical vs marketplace.

### F4 — Token passthrough / leakage — *mitigated by design*
**Threat (MCP §3.7, NHI2/NHI7):** forwarding a caller token to the upstream, or
leaking the bundle. **Today:** Tessera never receives a caller's upstream token,
and never returns/logs the resolved bundle — only a `present/absent` status
crosses the boundary. "Applications cannot leak what they don't have."

### F5 — SSRF / egress abuse — *not yet applicable, gated*
**Threat:** an egress broker that calls arbitrary upstreams is an SSRF engine.
**Today:** there is **no egress/injection call** in this slice, so the surface
does not exist yet. **Before adding injection:** an explicit upstream **egress
allow-list** (domain-pinned), enforced in the resolver→injector path.

### F6 — Supply chain / image trust — *standard mitigations*
**Threat (NHI3):** a malicious image runs with the KV SP. **Today:** image built
only by the repo's own GitHub Actions, pulled by the existing private GHCR secret,
runs non-root + read-only-root-fs + all caps dropped. **Future:** pin by digest;
sign (cosign) and verify on admission.

### F7 — the session may be dead — *handled gracefully, no medical action*
**Threat:** if `health-portal-session` has lapsed, "make it work" can't go
green. **Today:** the self-test reports `absent`/`incomplete` cleanly and Tessera
takes **no** remediating action (no login). Re-seeding remains a separate,
consent-gated, human-driven step. This is correct behavior, not a failure.

### F8 — DoS / resource exhaustion — *low, bounded*
**Threat:** request floods. **Today:** internal-only + netpol caps reachability;
pod has CPU/mem limits. **Future:** per-caller rate limits once the endpoint opens.

## Verdict

For the **current posture** — internal-only, read-only, fail-closed broker
endpoint, no upstream calls, secret-free audit — the residual risk is **low and
contained**, and the medical-account safety line is intact. The two items that are
strictly required **before** `POST /v1/broker` may be enabled for real callers:

1. **F1/F2:** a real caller-authentication plane (mTLS/SVID + signed end-user
   assertion, fully verified).
2. **F5:** an egress allow-list, added together with the injection step.

Until then Tessera is deployed as a *verified-wiring proof + decision/audit
spine*, which is exactly what this slice claims to be.
