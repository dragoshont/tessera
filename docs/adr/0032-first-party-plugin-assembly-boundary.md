# ADR 0032 - First-party plugin assembly boundary

- **Status:** Accepted (2026-08-11)
- **Supersedes in part:** ADR 0030's restriction that trusted-local plugins contain no executable code

## Context

Gmail, Regina Maria, and GitHub began as first-party integrations but their adapters, account flows, workers, configuration, capability factories, and model-tool bindings accumulated in `Tessera.Broker`, `Tessera.Core`, and the generic `Tessera.Providers` assembly. That made the host depend on provider schemas and made a missing provider assembly a startup failure rather than normal degradation.

The declarative package catalog remains the installation and historical-descriptor source of truth. It is not sufficient as the implementation boundary for first-party integrations.

## Decision

Each first-party provider is an independently built `Tessera.Plugins.*` assembly. A plugin assembly references `Tessera.Plugin.Abstractions`; Core, Broker, execution, and jobs never reference a plugin implementation assembly or type.

`Tessera.Plugin.Abstractions` owns the typed runtime seam:

- plugin and capability manifests;
- generic capability creation context;
- account definition and validation hooks;
- Chat and Job tool definitions and bindings;
- provider endpoint and hosted-service registration hooks;
- generic plugin configuration source; and
- a registry keyed by plugin ID and version.

Broker discovers explicitly configured assemblies from an operator-owned local directory. Discovery canonicalizes the root, rejects symlinks and paths outside it, inspects a bounded deterministic filename ordering, and fails closed for malformed or duplicate modules.

Registration is an atomic join between two trusted-local sources: the hash-pinned declarative package and one executable module. The module ID and version must exactly match an enabled package installation, and every executable capability must be declared by that package at the same version. A mismatch, duplicate, load failure, or undeclared capability exposes none of that module's services, endpoints, workers, tools, or capabilities. A disabled installation contributes nothing immediately. An absent module is not an application startup error: its provider remains unavailable and no connected-success state is synthesized.

Provider configuration is parsed and validated by its plugin. Broker passes only a generic configuration source containing the config document path and environment view. Legacy config sections and environment keys may remain compatible, but Core does not define or validate provider-specific option types.

Broker retains generic responsibilities only: principal and account ownership, credential-reference resolution, egress authorization and transport, capability dispatch, policy and approval enforcement, generic execution records, and evidence persistence. Provider DTOs, API/MIME/thread semantics, healthcare scheduling schemas, request construction, response normalization, and provider tool schemas remain in their plugin assemblies.

The CLI and packaging may arrange plugin files but do not introduce Broker/Core project references. Container images publish first-party plugin assemblies into the local discovery directory. No plugin secret is embedded in an assembly, manifest, image layer, or deployment document.

## Consequences

- A plugin can be removed or disabled without preventing Broker startup.
- Architecture tests enforce project, assembly, namespace, source, and provider-name boundaries.
- Provider tests reference the owning plugin project.
- Declarative manifests remain versioned product records while executable modules provide implementation behind the same stable IDs.
- Adding a provider requires a plugin project rather than edits to Broker dispatch switches or Core configuration.

## Verification

The boundary is complete only when:

1. Broker starts with Gmail or Regina Maria absent/disabled and reports their capabilities unavailable.
2. Runtime assemblies have no references to `Tessera.Plugins.*`.
3. Runtime source contains no Gmail, Regina Maria, or GitHub implementation identifiers except generic data values in tests/contracts explicitly allow-listed by the architecture test.
4. Existing provider behavior tests pass from plugin-owned test projects.
5. Traversal, symlink, malformed assembly, duplicate identity, package/module version mismatch, undeclared capability, and partial-registration attempts expose no provider contribution.
