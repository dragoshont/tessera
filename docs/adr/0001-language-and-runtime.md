# ADR 0001 — Language & runtime: .NET 10 (LTS)

- **Status:** Accepted (2026-06-13)
- **Deciders:** maintainer (Dragoș)
- **Supersedes:** the Python spike (v0.0.2), which proved the model and shipped a
  live read-only deployment. No backwards compatibility is required.

## Context

Tessera is, at its core, a **security boundary that is also a reverse proxy**: it
terminates mutual TLS, verifies cryptographic identities (X.509-SVID / OIDC /
RFC 8693), evaluates policy, talks to a secret store, and forwards upstream
requests with an injected credential. The qualities that matter most, in order:

1. **Auditability** — it is a credential broker; the secret-handling surface must
   be small, typed, and easy for the maintainer to reason about.
2. **First-class mTLS, JWT/JWKS, and crypto.**
3. **A production reverse-proxy** for the injection egress.
4. **Small, fast, single-artifact deployment.**
5. **Maintainer fluency** — the person who must audit every line should be fluent
   in the language.

A Python version was built first as a spike. It validated the design end-to-end
and was deployed read-only against a real vault. It is intentionally being
replaced, not extended.

## Decision

Build the broker in **.NET 10**, which is the current **LTS** release. Pin to LTS
/ GA libraries wherever possible:

- **Kestrel** for the HTTPS/mTLS listener.
- **YARP** (Yet Another Reverse Proxy) for the injection egress.
- **`Microsoft.IdentityModel.*`** for JWT/JWKS validation and token exchange.
- **`Azure.Identity` + `Azure.Security.KeyVault.*`** for the default store.
- **Native AOT** where feasible, for a small, fast, single-file artifact.

Target framework: `net10.0`. The session-harvesting tier stays a **separate
process** (see [ADR 0002](0002-broker-worker-topology.md) and
[ADR 0006](0006-harvest-drivers.md)), so the broker itself carries no browser or
emulator dependencies.

## Alternatives considered

### Go — *strong, rejected for this maintainer*
Go is arguably the most natural *ecosystem* fit: the entire adjacent category is
Go (CyberArk Secretless Broker, HashiCorp Boundary/Vault, Pomerium, Ory, SPIFFE's
`go-spiffe`), giving idiomatic patterns and contributor overlap. It produces
tiny static binaries and has excellent stdlib TLS. **It was a close call.** We
chose .NET because:

- For a **security-critical boundary the maintainer must personally audit**,
  maintainer fluency outweighs ecosystem adjacency. The maintainer's expertise is
  .NET.
- .NET matches the maintainer's existing stack, and **YARP** gives a
  batteries-included, well-tested reverse proxy for the egress.
- .NET interop with SPIFFE/JWT is good enough (X.509-SVID is just a certificate;
  JWT-SVID is just a JWT), so Go's first-class `go-spiffe` is not decisive.

This trade-off is recorded deliberately: ecosystem fit favored Go; auditability +
maintainer fluency favored .NET, and for a thing whose whole value is *trust*, the
latter wins.

### Node.js — *rejected*
Good async + native browser ecosystem, but `npm` dependency sprawl enlarges the
secret-handling attack surface — the opposite of goal #1 for a security product.

### Stay on Python — *rejected*
The spike was valuable but Python's mTLS/JWT/proxy story is weaker and the dynamic
typing makes the per-tenant-key + identity plumbing less auditable than typed DI.

## Consequences

- **Positive:** strong mTLS/JWT/crypto/proxy primitives; typed DI keeps the secret
  surface small and auditable; Native AOT yields a small fast artifact; the
  maintainer can audit confidently.
- **Negative:** we leave the Go-centric secretless/workload-identity ecosystem, so
  some patterns must be re-expressed in .NET rather than imported.
- **Mitigation:** the browser/Android automation that *is* native to other
  ecosystems lives in the separate harvest-worker tier, which can be written in
  whatever language suits that driver (Python/Playwright today).
