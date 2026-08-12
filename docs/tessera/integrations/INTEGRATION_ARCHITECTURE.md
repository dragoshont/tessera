# Integration Architecture

```text
Chat / Jobs / API
        |
Capability Registry + Policy + Actions
        |
Plugin mapping and authoritative risk overlay
        |
Tessera.Mcp.Client or justified native plugin adapter
        |
External integration / provider
```

Tessera owns principals, ConnectedAccounts, credential references, grants, context disclosure, Actions, approvals, executions, verification state, Evidence, Memory and Jobs. Integrations own provider authentication mechanics, protocol, identifiers, request/response shapes and session behavior.

An MCP tool is not automatically a Tessera capability. A hash-pinned plugin mapping assigns a stable capability ID, external tool name, account requirement, permissions, sensitivity, side-effect class, approval policy, idempotency and verification. Discovery checks compatibility. Extra tools are invisible; missing/incompatible tools degrade only the integration.

Provider plugins depend on `Tessera.Plugin.Abstractions` and optionally `Tessera.Mcp.Client`. Broker/Core never reference `Tessera.Plugins.*`. Packaging may copy plugin assemblies into an explicit discovery directory without project references.

Missing, disabled, untrusted, hash-mismatched or incompatible plugins contribute no endpoints, workers, tools or capabilities. Historical descriptors, Actions, Evidence and accepted Memory remain readable.
