# Tournament of Options

## Option A — Minimal Safe Fix

Document buffered Chat, shallow readiness, and manual file-copy backup; add only a live checklist. Smallest change, but fails the canonical Alpha bar for streaming, consistent backup, active health, and product truth.

## Option B — Proper Architectural Fix

Reuse the modular monolith and existing contracts: optional streaming transport capability, SQLite online backup, active component status, additive Account identity migration, provider-grounded permissions, canonical trace recovery, existing Job/Action/Memory abstractions, and focused UX repair.

## Option C — Defer / Ask More

Stop on missing credentials or redesign providers/scheduler. Rejected because external blockers do not prevent independent engineering and no product-policy question was unresolved.

## Decision Matrix

| Option | Spec fit | Risk | Complexity | Verifiable | Result |
|---|---:|---:|---:|---:|---|
| A | low | medium | low | partial | reject |
| B | high | controlled | medium | high | select |
| C | low | low immediate | high delay | low | reject |

## Winner

Option B. It fixes observed root causes while preserving provider-neutral state, custody, Actions, Memory, scheduler ownership, and the single-deployment topology.
