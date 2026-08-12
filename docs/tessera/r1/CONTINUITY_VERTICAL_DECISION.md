# Tessera R1 Continuity Vertical Decision

**Decision:** Follow-up continuity using a workflow-specific `FollowUp` aggregate and deterministic local source fixtures.

## Scoring Method

Scores are 1-5. Higher is better for product-value columns. For complexity, privacy risk, provider dependence, and execution dependence, 5 means greater cost or risk.

| Vertical | Frequency | Longitudinal value | State changes | History value | Prior-context benefit | Extraction complexity | Privacy risk | Provider dependence | Execution dependence | Deterministic corpus | Tessera proof |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Appointment | 3 | 3 | 4 | 3 | 3 | 2 | 4 | 4 | 2 | 5 | 2 |
| Follow-up / commitment | 5 | 4 | 5 | 5 | 5 | 3 | 3 | 1 | 1 | 5 | 5 |
| Travel | 2 | 3 | 5 | 3 | 3 | 4 | 5 | 5 | 3 | 4 | 1 |
| Project / obligation | 4 | 5 | 5 | 5 | 5 | 5 | 4 | 3 | 3 | 2 | 3 |

## Why Follow-Up Wins

Follow-up continuity gives prior accepted state an observable job. Later evidence can be incomplete by itself: "Monday works for it" or "Sent it to Rowan" becomes useful only when Tessera reconnects it to the accepted object, corrected deliverable, counterparty, and prior due date. This proves continuity rather than a connector.

The R0 Appointment decision was an experiment placeholder inherited from the prior product audit. The R1 instruction explicitly makes that audit historical rather than controlling. This decision supersedes Appointment as the R1 proof vertical only; it does not define Tessera's permanent product category.

## Representative Timeline

| Step | Evidence or user action | Expected result |
|---|---|---|
| 1 | "I will send the lease checklist to Rowan by 2026-08-14." | Deterministic extraction creates a candidate; user accepts it as current. |
| 2 | User corrects deliverable to "lease renewal checklist." | Correction becomes evidence and accepted current state; extracted value becomes historical. |
| 3 | "Monday instead works for it." | Accepted prior context links `it` and resolves due date to 2026-08-17; a new candidate awaits acceptance. |
| 4 | User accepts the revision and the process restarts. | Current and historical field provenance remain identical after restart. |
| 5 | "The Friday 2026-08-14 deadline still stands." | Credible incompatible evidence creates an explicit due-date conflict; neither value silently wins. |
| 6 | User resolves the conflict to August 17. | Resolution is correction evidence with both conflicting lineages preserved. |
| 7 | "Sent it to Rowan." | Prior accepted/corrected context resolves the object and creates a completion candidate. |
| 8 | User accepts completion and later asks Why. | Current state, ordered changes, correction, conflict, and field-level sources are available without replaying all source content. |

## Why The Alternatives Lose

- Appointment risks proving calendar parsing. Calendars already model invitations, reschedules, and cancellations; the prior audit biases the choice without proving Tessera-owned history is differentiated.
- Travel is infrequent, sensitive, and connector-shaped. Its strongest changes often come from provider-native status feeds.
- Project/obligation continuity is valuable but too broad for R1. It invites generic task, entity, and relationship abstractions before the continuity mechanism is proven.

## Adversarial Challenge

This slice is merely CRUD unless all of these are true:

1. A stateless extractor cannot safely resolve the incomplete third and seventh evidence items.
2. Persisted accepted and corrected context changes that handling deterministically.
3. Reprocessing old source cannot overwrite the correction or resurrect stale state.
4. Conflicting credible evidence remains visible until explicit resolution.
5. Every consequential field has an evidence-specific Why chain.
6. Restart preserves current/history/conflict distinctions.

Manual review is not the intelligence claim. The product claim is that review creates durable context that improves later interpretation.

## Scope Boundaries

- Use a workflow-specific `FollowUp`; do not introduce generic `Commitment`, `Situation`, `Entity`, graph, vector, or agent infrastructure.
- Use synthetic/local fixtures and a deterministic parser. No provider credential or cloud model is required.
- Persist provider-neutral source identity and metadata. Do not encode email, Microsoft, Google, or another provider into canonical continuity state.
- Build Attention, Tracked, Detail/Timeline, Why, and Correct surfaces. Do not build chat or external execution.
- Treat synthetic fixtures as proof of mechanics, not proof of market demand or frequency.