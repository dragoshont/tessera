# ADR 0005 — Verified identity first, fail-closed

- **Status:** Accepted (2026-06-13)
- **Deciders:** maintainer (Dragoș)

## Context

Every per-identity feature in Tessera — per-tenant isolation, "act on behalf of
this person," per-user envelope keys — is only as strong as the proof of *who is
asking*. A broker that trusts an unauthenticated *"I'm Manuela"* header is the
textbook **confused-deputy** vulnerability: anything that can reach it (a network
attacker, a prompt-injected tool, a compromised workflow) can request anyone's
credential. The MCP authorization spec and the OWASP Non-Human Identities Top 10
both center this risk.

## Decision

**Identity is cryptographically verifiable, or the request is denied.** Two
dimensions, both verified server-side, never inferred from request content:

- **WHO is calling** (the workload / non-human identity): **mTLS** client
  certificate / **SPIFFE X.509-SVID**, validated against a trusted root.
- **FOR WHOM** (the optional delegated human): a **signed OIDC / JWT** assertion
  (the RFC 8693 `subject_token`), validated for signature, `aud`, `exp`, `jti`,
  and an issuer allow-list.

Consequences of this rule:

- **The tenant is derived from the verified identity** (ADR 0004), as an ambient
  server-set value.
- **Default deny.** No matching grant → deny. No verified identity → deny. A
  fail-open policy is rejected at config validation.
- The network brokering endpoint **fails closed** until the caller-authentication
  plane is wired — exposing an unauthenticated credential path is the very
  vulnerability this project exists to prevent. (The Python spike already shipped
  this: `/v1/broker` returns `503` until auth is configured.)
- Prefer **X.509-SVID over JWT-SVID** for the workload where possible (JWTs are
  replayable; SPIFFE itself recommends X.509 when there's no L7 proxy in between).

## Consequences

- **Positive:** isolation and delegation rest on cryptography, not trust; the
  confused-deputy class is closed by construction.
- **Negative:** callers must be issued identities (mTLS/SVID + OIDC), which is real
  setup work and the gating item before the broker can be opened.
- **Mitigation:** ship issuance recipes (SPIRE / cert-manager for workloads; the
  IdP already in use for end-users), and keep a loopback-only `dev` mode for local
  experimentation that is refused on any network-exposed address.

## Rejected alternatives

- **Header/body-asserted identity** (`X-User: …`, `on_behalf_of` in the body as the
  source of truth) — rejected: this *is* the confused deputy. Such fields may ride
  the request for convenience but are authorized only against a separately
  *verified* assertion.
