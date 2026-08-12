# Regina Maria Reuse Decision

## Decision

`REUSE_LOCAL_MCP`

Use the existing local `reginamaria-mcp` through `Tessera.Mcp.Client`; do not copy its provider protocol into Tessera. It is Apache-2.0, exposes real Streamable HTTP MCP, and already implements identity, appointment reads, availability, preflight, booking and cancellation.

## Required Conditions

- Validate and release the currently dirty hardening changes; pin a commit and container digest.
- Run one isolated process/container and one credential/session chain per account owner.
- Authenticate MCP ingress and restrict network egress to required RM/identity/secret endpoints.
- Expose only the scheduling subset through Tessera's stable risk overlay; medical-record tools stay unavailable.
- Keep Tessera's exact Action approval and action-token gate for book/reschedule/cancel.
- Verify identity at connect and health checks; reject identity drift.
- Re-read provider state after mutation and reconcile unknown outcomes without blind retry.
- Never bypass fresh-login reCAPTCHA/MFA. The wife completes her own authorization.

## Known Risks

The connector is unofficial, one account per process, and uses a rotating single-use refresh chain. Its current README version text is stale relative to package metadata, its dependency constraints are lower bounds, and the working tree is not yet immutable. Those prevent deployment approval, not reuse development.
