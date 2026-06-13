# Architecture Decision Records

These ADRs capture the load-bearing design decisions for Tessera and *why* they
were made, so the reasoning survives past the conversation that produced it.

Each record is immutable once accepted; we supersede rather than edit.

| ADR | Decision | Status |
|---|---|---|
| [0001](0001-language-and-runtime.md) | Language & runtime: **.NET 10 (LTS)** | Accepted |
| [0002](0002-broker-worker-topology.md) | **Broker + capability-registered harvest workers** (co-located *or* separate, seamless) | Accepted |
| [0003](0003-credential-store-pluggable.md) | **Pluggable credential store**, Azure Key Vault default via Managed Identity / WIF | Accepted |
| [0004](0004-tenancy-and-isolation.md) | **Multitenant by default + optional dedicated instance**; per-tenant envelope keys; medical → dedicated | Accepted |
| [0005](0005-identity-first-fail-closed.md) | **Verified identity first, fail-closed**; tenant derived only from proven identity | Accepted |
| [0006](0006-harvest-drivers.md) | **Pluggable harvest drivers**: browser today; Android emulator & desktop as future drivers | Accepted |
| [0007](0007-worker-transport.md) | **Broker ⇄ worker transport: gRPC + mTLS** (typed contracts, bidi streaming) | Accepted |
| [0008](0008-policy-and-identity-administration.md) | **Policy/grants/bindings administration: file-first + GitOps** (admin API/UI a thin layer later) | Accepted |
| [0009](0009-end-user-identity-propagation.md) | **Per-call end-user delegation required**; shared MCP forwards a signed token; own/forked chat for it (+ WebRTC) | Accepted |

## Format

We use a lightweight [MADR](https://adr.github.io/madr/)-style format: Context →
Decision → Consequences, plus the alternatives we rejected and why.
