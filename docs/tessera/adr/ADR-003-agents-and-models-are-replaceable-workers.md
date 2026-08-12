# ADR-003: Agents and Models Are Replaceable Workers

- **Status:** Accepted for R0
- **Date:** 2026-08-09

## Context

Tessera benefits from model and agent reasoning but must not couple identity, memory, policy, or execution authority to a vendor or session.

## Decision

Represent replaceable reasoning through the small `IModelAdapter` contract over a sensitivity-bounded `ContextEnvelope` and structured output schema. An adapter may return proposals, confidence, references, identity/version, and transient diagnostics. Add a separate worker abstraction only when a second proven execution shape requires it.

Workers do not own persistence, grant policy, credentials, authorization, action transitions, or verification. Their output is untrusted until validated and explicitly accepted into Tessera domain state.

No production cloud model, model router, marketplace, multi-agent runtime, or live provider is selected by R0.

## Consequences

- Workers can be replaced without schema or evidence migration.
- Prompt injection cannot legitimately create authority.
- Context disclosure is explicit and minimal.
- Deterministic software remains responsible for authorization, hashing, state transitions, migrations, retries, and schema validation.