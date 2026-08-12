# ADR 0028 — R2 in-product durable Chat

- **Status:** Accepted (2026-08-10)
- **Supersedes for R2:** [ADR 0010](0010-chat-client.md)

## Decision

R2 Chat is implemented in the existing Tessera React web application and Broker host with owner-scoped SQLite persistence and the shared ExecutionCoordinator. The external LibreChat fork is not the R2 product dependency. Existing identity and MCP decisions remain relevant, but Chat state, context, approvals, and capability results are Tessera-owned.

## Consequences

R2 can enforce one owner/context/action contract and restart durability without a second database or execution engine. LibreChat reuse may be reconsidered after Alpha only if it can bind to these canonical APIs without owning memory or authorization.
