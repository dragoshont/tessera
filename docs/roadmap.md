# Tessera — Build plan (single iteration)

There is **one** iteration planned right now, and **everything committed lands in
it**: the broker, the full security hardening, per-user delegation, the chat
surface with **WebRTC voice**, and the harvest workers. Nothing load-bearing is
deferred to a "later iteration" — that bucket does not exist.

What follows is **build order within that one iteration** (you must build the core
before the egress that uses it), not a list of maybe-laters. Security is woven
through every workstream, not a phase at the end.

> The Python spike (v0.0.2) is archived (`*.python-spike.md`). It proved the model
> and ran live read-only. The .NET 10 build replaces it; no backwards compat.

---

## Definition of done (the whole iteration)

The iteration is complete when **all** of the following are true and verified:

- **Verified identity, fail-closed** — mTLS / SPIFFE X.509-SVID for the workload ⊕
  signed OIDC for the end-user, validated (sig, `aud`, `exp`, `jti`, issuer);
  tenant derived from identity; default deny. ([ADR 0005](adr/0005-identity-first-fail-closed.md))
- **Per-call end-user delegation** — a shared MCP can serve many users, each
  forwarding their **own signed token**; Tessera verifies both badges.
  ([ADR 0009](adr/0009-end-user-identity-propagation.md))
- **Chat surface that delegates** — extend an existing chat *or* ship our own
  minimal chat that propagates per-user identity, **including WebRTC voice** as a
  first-class consumer. ([ADR 0009](adr/0009-end-user-identity-propagation.md))
- **Secretless transit + injection egress** — Azure KV via Managed Identity / WIF
  (no client secret); YARP egress with SSRF allow-list; inject, never hand over.
  ([ADR 0003](adr/0003-credential-store-pluggable.md))
- **Per-tenant isolation** — envelope key per tenant; dedicated-instance tier for
  medical. ([ADR 0004](adr/0004-tenancy-and-isolation.md))
- **Harvest workers** — browser driver, gRPC+mTLS capability registration,
  co-located *or* separate deployment, identical client contract.
  ([ADR 0002](adr/0002-broker-worker-topology.md) · [0007](adr/0007-worker-transport.md))
- **Hardened by default** — every threat-model mitigation in
  [architecture.md §6](architecture.md) is on by default: `exp`+`jti` required,
  egress allow-list, rate limits, content-size limits, step-up for write/pay/book,
  secret-free audit, sandboxed workers, file-first GitOps policy with
  deny-by-default. ([ADR 0008](adr/0008-policy-and-identity-administration.md))

---

## Build order (within the one iteration)

Phases here are **sequencing**, not scope-gating — all are committed.

1. **Core** (`Tessera.Core`) — identity/decision/tenancy model, fail-closed PDP,
   recipe + grant + binding model, file-first config loader with validation. Pure,
   no I/O, xUnit. *(Done = the rulebook + vocabulary everything else builds on.)*
2. **Identity plane** (`Tessera.Identity`) — Kestrel mTLS; verify SVID/client-cert
   (WHO) and signed OIDC (FOR WHOM); RFC 8693 token exchange; **per-user
   delegation** end-to-end; tenant from identity. *(This is the security gate.)*
3. **Store + injection egress** — `ICredentialStore` + Azure KV (MI/WIF) +
   per-tenant envelope keys; YARP egress with SSRF allow-list; secret-free audit;
   `tessera serve`.
4. **Harvest workers** — driver contract + gRPC+mTLS registration + capability
   router; browser driver (wraps the Python harvester); co-located and separate
   topologies; `driven` `act()` channel.
5. **Chat surface + WebRTC voice** — extend or build the chat that propagates
   per-user identity; wire text + voice consumers; step-up approval UX for
   high-impact actions.
6. **Tenancy/governance hardening** — dedicated-instance stamp for medical;
   lifecycle TTLs + revoke-on-offboard; finalize hardening defaults.

(Independent workstreams — e.g. the chat client and the browser worker — can
proceed in parallel once Core + Identity exist.)

---

## Genuinely demand-driven (not this iteration's commitment, by nature)

These are **extension points designed-in now**, built when a concrete provider
needs them — they are demand-driven, not iteration-deferred:

- **Android emulator driver** and **desktop driver** — for app-only / cert-pinned
  providers ([ADR 0006](adr/0006-harvest-drivers.md)). The driver contract and
  worker topology already accommodate them; we build one when a target requires it.
- **Non-default stores** beyond Azure KV (Vault/OpenBao; Vaultwarden as a test
  backend) — the `ICredentialStore` seam exists from day one
  ([ADR 0003](adr/0003-credential-store-pluggable.md)).

---

## On a separate admin UI

Not required to function — the broker is **operable headlessly** (file-first
GitOps + CLI). If we build our own chat ([ADR 0009](adr/0009-end-user-identity-propagation.md)),
**that chat is the human surface**, and any admin screens are a thin layer over the
same file-backed model — never a place where a security decision secretly lives.

> Rule of thumb: a UI is a convenience over the API; authorization always remains
> the reviewable, version-controlled source of truth.
