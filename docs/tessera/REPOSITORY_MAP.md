# Tessera Repository Map

## Project Dependency Map

```text
Tessera.Broker
  -> Tessera.Core
  -> Tessera.Identity
  -> Tessera.Mcp
  -> Tessera.Providers
  -> Tessera.Stores.AzureKeyVault

Tessera.Identity -> Tessera.Core
Tessera.Mcp -> Tessera.Core + Tessera.Identity
Tessera.Providers -> Tessera.Core
Tessera.Stores.AzureKeyVault -> Tessera.Core
Tessera.Cli -> broker/core composition
```

`Tessera.Core` intentionally has no external package dependency and remains the domain dependency root.

## Current Broker Request Path

```text
HTTP/MCP caller
  -> OIDC validation
  -> CallerIdentity + optional EndUserAssertion
  -> PolicyDecisionPoint
  -> CredentialResolver
  -> ICredentialStore
  -> credential injection / provider transport
  -> secret-free audit
```

Key locations:

- Composition: `src/Tessera.Broker/BrokerHost.cs`
- Caller API: `src/Tessera.Broker/CallerBrokerEndpoint.cs`
- MCP API: `src/Tessera.Mcp/TesseraMcpTools.cs`
- Policy: `src/Tessera.Core/Policy/PolicyDecisionPoint.cs`
- Resolution: `src/Tessera.Core/Resolution/CredentialResolver.cs`
- Egress: `src/Tessera.Providers/ProviderEgress.cs` and `src/Tessera.Broker/EgressProxyEndpoint.cs`

## Portal Path

```text
React SPA
  -> /portal/* endpoint
  -> forwarded OIDC token validation
  -> PortalService projection/mutation
  -> policy snapshot + credential status
```

The portal exposes metadata/presence only. It does not expose credential values.

## Audit Path

`IAuditSink` receives authorization decisions. `JsonlAuditSink` is durable output; `RingBufferAuditSink` adds a volatile portal tail. Product provenance is not implemented and must remain distinct from this security audit.

## Write Approval Paths

- Raw proxy writes: server-issued `PendingWrite`, exact content hash, portal decision, single-use consume.
- Named provider writes: caller-controlled boolean currently bypasses independent approval. This is a verified Kernel v1 blocker.

## Principal Rules

- App caller identity uses a signed application ID.
- End-user subject uses signed OIDC claims.
- Existing grant/binding paths may compare either subject or `preferred_username`.
- Kernel product state requires a canonical `(issuer, tenant, subject)` identity; display names are non-authoritative.

## Persistence State

There is no product-state database. Durable state today is split across:

- policy files for grants/bindings/recipes;
- credential store for secret bundles;
- JSONL for security audit.

Volatile stores include write challenges, connection health, OAuth pending state, consent projection, and audit tail.

## Kernel v1 Extension Boundary

```text
Tessera.Core/Kernel
  immutable domain records, state machines, context/capability/worker contracts,
  and persistence ports

Tessera.Persistence.Sqlite
  SQL schema/migrations and owner-scoped implementation of Kernel ports

Tessera.Broker
  optional future composition/API; current routes remain unchanged by default
```

Kernel persistence never stores credentials, grants, bindings, or security-audit payloads.

## Test and Gate Map

- Core unit tests: `tests/Tessera.Core.Tests/`
- Broker endpoint/integration tests: `tests/Tessera.Broker.Tests/`
- Provider tests: `tests/Tessera.Providers.Tests/`
- Identity tests: `tests/Tessera.Identity.Tests/`
- Web unit tests: `web/src/**/*.test.tsx`
- Web E2E: `web/tests/`
- Full web gate: `gates/checks.sh`
- Backend/IaC gate: `gates/backend-checks.sh`

## Kernel File Ownership

- Manager: shared manifests, existing trust paths, integration.
- Persistence employee: new Kernel domain/persistence/test files only.
- Documentation employee: new `docs/tessera/**` files only.
- Adversaries: read-only review plus regression-test recommendations; manager integrates fixes.