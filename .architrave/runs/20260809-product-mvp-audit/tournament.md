# Tournament of Options

## Option A — Deterministic ICS-Only Continuity

Lowest risk and no model dependency, but too narrow to prove continuity across ordinary appointment messages.

## Option B — Read-Only Appointment Continuity

Proves evidence, current/history, correction, reversible correlation, privacy, and model replacement without external writes. This is the mandatory first release gate.

## Option C — Appointment Continuity Plus Calendar Reconciliation

Adds one content-bound, idempotent, verified external loop. This is the recommended complete MVP after Option B passes.

## Option D — Email Follow-Up And Send

Useful, but duplicate or incorrect communication has a larger social blast radius and harder verification.

## Option E — Regina Maria Booking

Adds health sensitivity, session rotation, and provider-specific booking before continuity is proven.

## Option F — Full World-State Substrate

Implements the v0.9 conceptual model before any workflow proves that the abstractions are necessary.

## Decision Matrix

| Option | Product proof | Risk | Reuse | Decision |
|---|---:|---:|---:|---|
| ICS-only | Medium | Low | Medium | Spike only |
| Read-only appointments | High | Low-medium | High | Mandatory gate |
| Appointments plus calendar | Highest | Medium | High | Complete MVP |
| Follow-up send | High | High | Medium | Defer |
| Regina Maria booking | Medium | Very high | Medium | Later vertical |
| Full substrate | Low initially | Very high | Low initially | Reject |

## Winner

Option C, delivered only after Option B passes. It is the smallest slice that proves both durable continuity and safe execution while preserving a low-risk stop point.
