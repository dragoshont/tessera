# ADR-001: Tessera Modular Monolith

- **Status:** Accepted for R0
- **Date:** 2026-08-09

## Context

Tessera needs durable domain semantics, trust boundaries, persistence, capabilities, and replaceable workers. There is no current scaling or isolation evidence requiring distributed services, and the repository already has clear .NET project boundaries.

## Decision

Keep Tessera as a modular monolith. `Tessera.Core` owns dependency-free domain contracts and ports; the integrated `Tessera.Persistence.Sqlite` adapter depends inward on Core. `Tessera.Broker` currently composes Core, identity, provider, and credential-store adapters; future Kernel runtime integration may also compose the SQLite adapter.

Logical experience, knowledge, intelligence, execution, capability, and trust planes are modules, not deployment units. Do not create one project or service per logical plane without current evidence.

## Consequences

- Transactions and local reasoning remain simple.
- Existing broker trust assets are preserved rather than rewritten.
- SQLite and provider details stay outside Core.
- Future extraction is possible through existing ports if measured needs justify it.
- Distributed discovery, messaging, and consistency are not introduced in R0.