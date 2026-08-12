# Plugin Discovery

## Sources

- Local Tessera package catalog: hash-validated built-ins and installed state.
- Official MCP Registry: public server/package/remote metadata.
- Provider-owned public repository adapter: MCP-oriented repository metadata.

Broker owns the provider-neutral source contract, normalization, ranking, caching and source degradation. Provider-specific repository API endpoint, headers and response parsing are behind `ITesseraCatalogPlugin`, preserving the Core/Broker boundary.

## Result Model

Results include name, description, source, publisher, runtime, repository/package, version, license, trust, capabilities, auth types, sensitivity, installation mode/state and inspect URL.

## Trust Policy

- Local hash-validated packages: `BUILT_IN` and installed/available.
- Active official-registry metadata with a repository: `VERIFIED_METADATA`.
- Public repository results: `UNTRUSTED`.
- Public results: always `SERVER_REVIEW_REQUIRED` / `REVIEW_REQUIRED`.

Search never downloads, executes or installs public code. Inspect accepts only safe HTTPS targets. Sensitive mailbox/calendar/health descriptions receive elevated sensitivity labels.

## Caching and Failure

Official registry queries cache for 30 minutes; provider-owned public repository queries cache for one hour. Each source reports Ready or Degraded independently so a slow external catalog does not hide local installed integrations.

## Verification and Limits

Tests cover local installed/hash state, official MCP Registry normalization/cache, generic provider-source normalization/cache, provider-owned repository request/parsing, source metadata and Web/iOS search presentation. The delivered Alpha intentionally does not offer one-click installation of untrusted Internet code; installation requires a future explicit manifest/runtime/network review workflow.