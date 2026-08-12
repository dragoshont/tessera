# Live Model Verification

## Status

`BLOCKED_EXTERNAL`

No OpenAI-compatible endpoint/model credential was available in the environment or configured through Tessera custody. Contract tests never count as live PASS.

## Implementation

The adapter supports HTTPS remote endpoints and explicit loopback HTTP, `/models` validation, bearer authentication, SSE `chat/completions`, UTF-8-safe arbitrary chunks, bounded text/tool reconstruction, `[DONE]` enforcement, tools and continuation, cancellation, 401/403, 429, timeout, malformed response, and 1 MiB transport bounds.

Text deltas use canonical SSE `text` events with `{delta}` and stable `live-N` IDs. Deltas are owner/conversation/execution-bound, process-local, globally bounded, and expire. Final output is recursively validated before one durable assistant message is written.

## Contract Evidence

- split multibyte UTF-8 and JSON chunks: PASS;
- streamed tool reconstruction: PASS;
- missing `[DONE]`: `provider_malformed`;
- full Chat stream/tool/Stop/retry/restart integration: PASS;
- remote model private-address SSRF guard: PASS.

## Exact Live Step

1. Start Tessera.
2. Configure and validate a model in Settings.
3. Run `./gates/live-alpha-checks.sh`.
4. Require `OpenAI-compatible chat PASS` and `OpenAI tool call PASS`.

The script uses Tessera’s configured profile/custody and never accepts or prints a provider secret.