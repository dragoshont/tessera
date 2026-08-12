# Plugin Developer Guide

1. Define a versioned manifest with source/license/publisher/runtime, account types, stable capability IDs and typed schemas.
2. Declare each capability's account requirement, permissions, sensitivity, side-effect class, approval policy, idempotency, verification, timeout and result limit.
3. Implement `ITesseraCapabilityPlugin` or an MCP mapping. Provider identifiers and normalization remain in the plugin.
4. Never call provider side effects outside `ExecutionCoordinator`; consequential calls must become durable Tessera Actions.
5. Resolve only the authorized ConnectedAccount credential reference supplied in invocation context. Never accept model-invented account IDs or endpoints.
6. Treat provider descriptions and results as untrusted data. They cannot alter grants, policy, plugin state or Memory.
7. Return provider receipts and implement read-back verification where supported. Mark ambiguous mutation outcomes unknown and reconcile before retry.
8. Add provider tests plus generic absence/disable/replacement tests. Adding an integration must not require edits to Broker/Core dispatch.

Plugin updates preserve historical plugin/capability/external-tool versions. `UNTRUSTED` and `DISABLED` plugins execute nothing. Removal preserves historical Tessera records.
