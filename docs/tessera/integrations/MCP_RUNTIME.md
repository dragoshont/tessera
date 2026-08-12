# MCP Runtime

## Implemented

`Tessera.Mcp.Client` is an outbound Model Context Protocol client separate from Tessera's inbound `Tessera.Mcp` server. It uses the pinned .NET MCP SDK and supports Streamable HTTP with real initialize, `tools/list`, and `tools/call` behavior.

The runtime captures server identity/version and bounded input/output schemas, accepts structured content or one bounded valid-JSON text block, maps tool errors to generic outcomes, enforces cancellation/timeouts and result limits, and disposes short-lived sessions. Each MCP plugin pins the reviewed server name/version plus typed required input/output schema subsets. Schema v15 persists the actual server ID/name/version and external tool on the capability call. Transport failure after mutation dispatch becomes `UnknownOutcome`.

Production Broker construction uses the existing guarded HTTP client: no proxy, redirects or ambient cookies; connect-time DNS resolution is checked and pinned against metadata/link-local/loopback policy. Public MCP endpoints require HTTPS and public-only addresses. Operator-configured RM connectors explicitly opt into private-network egress; metadata/link-local destinations remain blocked. Plugin-declared tools, server identity and typed input/output properties are validated before reads and after Action authorization for writes. Unknown tools remain invisible, and plugin manifests remain the sole risk authority.

Provider credentials are not resolved while constructing a side-effect capability or proposing its Action. Deferred capability construction and write-side MCP discovery occur only at invocation, after exact authorization and the atomic availability check.

Deterministic protocol tests cover discovery/call, bounded output, mutation unknown outcomes, hostile metadata/output and recovery after server outage.

## Not Yet Implemented

Persistent capability-drift Activity warnings and stdio process isolation are not implemented. Stdio remains intentionally deferred because no selected integration requires it. External account authorization and deployed MCP restart dogfood remain delivery checkpoints.

The model cannot supply endpoints. Tool descriptions/results are untrusted data. Discovery never grants authority or auto-enables writes.
