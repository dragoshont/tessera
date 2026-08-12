# R2 Chat Model

Conversation is owner-scoped and has title, state (`ACTIVE`, `ARCHIVED`, `DELETED`), optional model-profile override, timestamps, and version. Message has role (`USER`, `ASSISTANT`, `SYSTEM_EVENT`, `CAPABILITY`), status, timestamps, retry parent, and version. MessagePart stores only public text, status summary, capability request/result, approval reference, evidence citation, or failure. `CapabilityCall`, `CapabilityResult`, `ContextSnapshotReference`, and sequenced `ExecutionEvent` are durable records.

Send commits the user Message before invoking a model. Outage leaves it persisted and creates a retryable failed assistant turn. Stop persists cancellation; late transport output is ignored by execution generation. SSE resumes from durable sequence. Context uses the existing bounded `ContextEnvelope`; no entire-database dump, hidden prompt, or chain-of-thought is persisted or streamed.
