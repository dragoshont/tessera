# R2 Memory In Chat

Conversation history and durable personal state are separate. Ordinary text remains a Conversation Message. Explicit Remember creates R0 user Evidence plus a current Assertion with the message as provenance. Correct creates new user Evidence, supersedes the prior Assertion atomically, and preserves history. Why projects exact evidence, timestamp, previous/current value, and lineage; it is not generated rationale.

ContextBuilder receives bounded relevant accepted Assertions, FollowUps, Evidence excerpts, Jobs, account/capability availability, and recent messages under sensitivity and size budgets. Candidate/conflicted state is labeled uncertain. R2 Alpha exposes only `stop-using`, which excludes state from future context while retaining history.

The explorer reads current/history Assertions, chronological correction and stop-using changes, current FollowUps with field Evidence, and bounded Activity projections. Search is literal and owner-scoped. `Why` and history dereference exact Evidence/lineage; they do not generate explanations.

Physical forget/remove is an explicit R2 Alpha defer, not an implicit omission: the current deployment has no approved backup, retention, or erasure contract that can truthfully guarantee deletion across evidence lineage and recovery media. The UI and API therefore say "Stop using" and never claim erase/forget. A future destructive lifecycle requires a settled retention/backup ADR and migration-safe deletion contract before exposure.
