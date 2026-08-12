# Tessera Kernel Architecture

## Status

R0 architecture foundation. This document describes integrated contracts and boundaries, not a launched personal-continuity product.

## Modular Monolith

Tessera remains a .NET modular monolith:

```text
Tessera.Broker (host and composition)
  -> Tessera.Core (domain, trust, Kernel ports)
  -> Identity / MCP / Providers / credential-store adapters

Tessera.Persistence.Sqlite -> Tessera.Core
Tessera.Core -> no project/package dependency

Future Kernel runtime composition:
Tessera.Broker -> Tessera.Persistence.Sqlite
```

Logical planes do not imply services or one project per plane.

## Module Ownership

### Core Kernel

`Tessera.Core.Kernel` owns provider-neutral immutable records, validation, state transitions, context construction, capability and worker contracts, and persistence ports.

Core does not authenticate callers, evaluate broker grants, access credentials, select providers, issue trusted human approval, or perform HTTP/database work.

### SQLite Adapter

`Tessera.Persistence.Sqlite` owns schema versions, migrations, transactions, owner-scoped queries, optimistic version checks, and implementation of Kernel persistence ports.

Its tables are limited to principals, evidence, observation events, assertions, actions, action authorizations, and workflow checkpoints. The schema defines no credential, grant, binding, broker security-audit, prompt, model/worker-output, diagnostics, or secret columns. Because several domain fields are generic text/JSON, application validation must also prevent prohibited content from being placed in those fields; the schema-name test alone is not a content-leakage proof.

### Broker Trust Plane

The existing broker remains the trust plane: caller and end-user authentication, deterministic grant policy, credential custody/resolution, SSRF-constrained egress, independently authorized writes, and secret-free security audit.

Kernel contracts do not replace or bypass the broker. The Broker does not yet compose the SQLite Kernel adapter at runtime. A future live capability path must add that composition and combine broker policy/out-of-band approval with Kernel action reservation.

### Replaceable Boundaries

- `IModelAdapter` is the single replaceable computation port in R0.
- `ICapability` is a versioned invocation port.
- Kernel repositories isolate durable state from SQLite-specific details.
- Provider-specific payload normalization and verification belong in adapters.

## Data Flow

```text
trusted source/user input
  -> owner-scoped EvidenceRecord
  -> append-oriented ObservationEvent
  -> candidate AssertionRecord
  -> deterministic/user-approved promotion or conflict
  -> sensitivity-bounded ContextEnvelope
  -> replaceable worker proposal
  -> broker policy + independent approval
  -> atomic authorization/action reservation
  -> capability invocation
  -> execution, provider verification, external confirmation
```

The latter execution path is a contract boundary, not authorization for live writes.

## Governing Decisions

- [ADR-001](adr/ADR-001-tessera-modular-monolith.md)
- [ADR-002](adr/ADR-002-durable-state-owned-by-tessera.md)
- [ADR-003](adr/ADR-003-agents-and-models-are-replaceable-workers.md)
- [Security boundaries](SECURITY_BOUNDARIES.md)
- [Domain model](DOMAIN_MODEL.md)