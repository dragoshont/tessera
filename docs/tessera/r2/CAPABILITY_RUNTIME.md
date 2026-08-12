# R2 Capability Runtime

`CapabilityAvailabilityService` intersects principal ownership, enabled validated plugin/version, connected account and permissions, conversation/job account and capability grants, side-effect policy, and model tool support. It returns available or a stable blocked reason and runs again immediately before dispatch.

The generic HTTP executor accepts only manifest-declared fixed host, method, path template, query/body schema, credential binding, timeout, result limit, normalization, side-effect class, and verification strategy. Template values cannot change scheme/host/port or inject path traversal. DNS/IP checks preserve SSRF guards. Results are bounded, treated as untrusted data, and cannot authorize or directly mutate Chat.

Provider origins, headers, identity probes, resource allow-lists, stable capability mappings, request/response schemas, receipt parsing, and read-back verification belong to the provider plugin or MCP mapping. The generic runtime applies the declared risk overlay and exact Action binding without interpreting provider semantics. Unknown outcomes enter reconciliation without blind retry.
