# ADR-002: Durable State Is Owned by Tessera

- **Status:** Accepted for R0
- **Date:** 2026-08-09

## Context

Model sessions, agents, provider APIs, and chat histories are replaceable and transient. Tessera must preserve accepted state, provenance, history, correction, and action progress across process and worker replacement.

## Decision

Tessera owns canonical principal, evidence, observation, constrained assertion, action, authorization-reservation, and workflow state behind Core persistence ports. SQLite is the current transactional adapter.

The Kernel schema defines no dedicated credential, grant, binding, broker security-audit, raw-prompt, model/worker-output, diagnostics, or secret columns. Runtime output should become durable only through an explicit validated domain transition; generic fields require producer validation and content-leakage tests.

Generic Assertion is constrained internal current/history infrastructure, not a product
ontology. **R1 update:** the Appointment experiment placeholder is superseded by the
workflow-specific FollowUp proof vertical; this does not make FollowUp permanent.

## Consequences

- Replacing a model or worker does not migrate canonical state.
- Product provenance and security audit remain separate.
- Credential custody and policy retain their existing owners.
- Backup, restore, forget, and complete erasure require later end-to-end product/deployment work and are not implied by the port.