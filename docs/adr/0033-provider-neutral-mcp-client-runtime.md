# ADR 0033 - Provider-neutral MCP client runtime

- **Status:** Accepted (2026-08-11)
- **Depends on:** ADR 0032

## Context

Tessera already exposes an inbound MCP server to Chat clients, but provider integrations need an independent outbound MCP client. The existing Regina Maria integration manually speaks JSON-RPC and provider code in Broker constructs its capabilities. That is neither a reusable MCP runtime nor the required dependency boundary.

## Decision

`Tessera.Mcp.Client` is the provider-neutral outbound MCP client. It uses the actual Model Context Protocol SDK and currently supports streamable HTTP because the selected Regina Maria and candidate remote integrations use it. Stdio is deferred until a selected integration requires it.

The runtime owns initialization, server identity/version, tool discovery, schema capture, invocation, cancellation, bounds, lifecycle disposal, and generic error outcomes. It does not assign stable capability IDs, trust provider tool descriptions, classify side effects, choose accounts, resolve credentials, approve actions, or interpret provider payloads.

Every invocation uses an operator-configured endpoint. The caller supplies a guarded `HttpClient`; arbitrary model-supplied URLs are invalid. Sessions are short-lived and are not replayed. A transport failure after mutation dispatch is `UnknownOutcome`, never an automatic retry.

Provider plugins map external tools to stable Tessera capabilities and authoritative risk overlays. Discovery proves compatibility; it does not grant authority. Missing or incompatible required tools fail closed, and newly discovered writes remain unavailable until classified.

## Consequences

- Inbound `Tessera.Mcp` and outbound `Tessera.Mcp.Client` remain separate.
- Provider plugins may reference the client runtime; Core and Broker never reference concrete plugins.
- Streamable HTTP is implemented first. Stdio requires a real consumer and explicit process isolation design.
- Test coverage uses a deterministic real-protocol MCP server, not a fake MCP-shaped interface.
