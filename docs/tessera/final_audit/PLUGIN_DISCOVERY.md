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

## Reviewed Installation

Search → Review installation → Install is available only for an exact `id@version` already present in the server's hash-pinned local catalog. Web and iOS display publisher, version, runtime, trust, sensitivity, capabilities and authorization requirements before confirmation. The keyed server endpoint re-resolves the package from the catalog and installs it disabled; clients cannot submit a manifest, command, endpoint, hash or trust level. Enable, configuration and account authorization remain separate actions.

Public MCP Registry/repository results remain Inspect-only until an operator puts a reviewed artifact into the server catalog. There is no arbitrary download, shell execution or remote-package install path.

## Caching and Failure

Official registry queries cache for 30 minutes; provider-owned public repository queries cache for one hour. Each source reports Ready or Degraded independently so a slow external catalog does not hide local installed integrations.

## Verification and Limits

Tests cover exact local `id@version` state, disabled/idempotent/owner-scoped installation, unknown-package refusal, official MCP Registry normalization/cache, generic provider-source normalization/cache, provider-owned repository request/parsing, Web review confirmation/canonical refresh and iOS review/type safety. All five reviewed packages were already installed for the live owner, so no canonical package was removed merely to manufacture a live installation target; deterministic backend and client E2E provide the install proof.