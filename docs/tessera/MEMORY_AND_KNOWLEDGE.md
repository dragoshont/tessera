# Memory and Knowledge

## Durable Memory

Tessera-owned durable memory consists of canonical owner identity, evidence references/excerpts under retention policy, append-oriented observation events, constrained assertions and their history, action/workflow state, provenance, and version metadata.

This state survives replacement of a model, worker, provider adapter, or chat session.

## What Is Not Memory

- A prompt is not canonical state.
- Model or worker structured output is not accepted belief.
- Diagnostics are not product memory.
- Embeddings, if ever introduced, are indexes rather than evidence.
- Broker security audit is not product provenance.
- Credentials, grants, and bindings are not Kernel knowledge.

The SQLite Kernel schema has no dedicated columns for raw prompts, structured/model/worker output, diagnostics, or secret material. Runtime request/result contracts may carry structured output and diagnostics transiently; the domain contract requires an explicit validated transition before persistence. Generic text/JSON fields still require producer validation, so schema inspection alone does not prove content-level exclusion.

## Knowledge Evolution

1. Evidence is recorded under a canonical owner.
2. Observation is appended without rewriting history.
3. Extraction or inference creates a candidate with producer/version and provenance.
4. Deterministic rules or explicit user action may promote supported state.
5. Correction creates new user-authored state and supersedes the prior current value.
6. Unresolved credible disagreement becomes explicit conflict.

Generic `AssertionRecord` is intentionally constrained infrastructure for this sequence. It does not establish an ontology or authorize automatic graph population.

## Context

`ContextEnvelope` is a deterministic, bounded view over selected state. It orders by relevance/time, filters by allowed sensitivity, records omissions, and hashes the resulting structure. It is disposable and non-canonical.

## Product Boundary

R1's proof slice is FollowUp-specific, with field-level provenance, revisions,
corrections, conflict, and current/history behavior. This supersedes Appointment only
as the R1 vertical; generic Claim, Entity, Situation, Commitment, Preference, graph,
and semantic retrieval remain gated by measured product failures.

## Lifecycle Gaps

Retention states exist, but complete backup, restore, forget, erasure-journal, and derived-state rebuild behavior are not established by R0. Those require product/deployment implementation and end-to-end tests.