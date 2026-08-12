# Capability Risk Overlays

MCP metadata is advisory only. Each exposed tool requires a Tessera-owned overlay containing stable capability ID/version, exact external tool name, account type, permissions, input/output schemas, sensitivity, context classes, side-effect class, approval policy, idempotency, verification, timeout and result bound.

Unknown tools default to deny. Extra discovered tools are invisible. A missing tool blocks its capability. An incompatible schema marks it degraded. A new or changed write never auto-enables.

Read tools execute only after account/grant/policy checks. Consequential tools create an Action bound to principal, account, plugin/version, capability, external tool, normalized payload, target, expiry and single use. Any change invalidates approval.

Tool descriptions and outputs are untrusted provider data. Prompt injection cannot authorize another call, change policy, install plugins or write Memory.
