# Chat Reality Report

## Result

Internal/contract: PASS
Live model: BLOCKED_EXTERNAL

## Verified

- production UI creates and persists conversations/messages;
- accepted user messages survive provider failure;
- duplicate idempotency retries do not duplicate user messages;
- SSE renders transient model text and one final durable assistant message;
- streamed tool calls reconstruct and continue through Tessera;
- Stop cancels transport and records deterministic STOPPED state;
- retry has explicit lineage;
- browser refresh discovers durable active execution and reattaches;
- backend restart resets only interrupted RUNNING traces; completed reads replay durable results without duplicate invocation;
- capability, Action, approval, Evidence, and failure events reconstruct from backend state;
- cross-principal transient and durable access is denied.

## Trust And Retention

Transient text is never canonical and is not stored in SQLite. It is recursively validated before final persistence. Common GitHub/OpenAI/Slack/JWT credential formats and credential-like JSON properties are rejected. Prompt/context text is not persisted in model capability traces.

## Recovery UX

Known failures map to actionable copy describing what failed, what Tessera preserved, and how to recover. Raw execution codes remain in backend records for diagnostics, not primary user copy.

## Limitation

A backend restart during the external HTTP stream cannot resume at a provider byte offset; Tessera safely restarts the read-only logical call. Consequential operations use Actions/reconciliation and are never blindly retried.