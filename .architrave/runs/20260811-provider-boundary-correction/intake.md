# Intake

## Understanding

Correct the first-party provider boundary without changing connector behavior: Gmail, Regina Maria, and GitHub become dedicated plugin assemblies composed through a provider-neutral runtime contract. Preserve the intentionally dirty tree and all current security behavior, especially Regina Maria canonical booking/cancellation and action-token handling.

## Acceptance Criteria

1. Three explicit `Tessera.Plugins.*` projects own provider implementation and lifecycle code.
2. Broker/Core/Providers contain no provider implementation schemas, normalization, tool switches, or typed provider configuration.
3. Runtime assemblies reference contracts only and discover plugins generically; absent/disabled plugins are normal.
4. Typed manifests, generic capability registry, account hooks, and Chat/Job tool contracts form the seam.
5. Existing config/env remains compatible through plugin-owned parsing.
6. Architecture tests reject implementation references and source/type leakage.
7. Startup tests prove honest Gmail/RM absence and disable behavior.
8. Provider behavior and security tests remain executable under plugin test ownership.
9. Solution/container manifests ship assemblies without secrets; no deployment or external mutation occurs.
10. Focused and repository gates plus two-family semantic review pass.

## Grounding Sources

`architrave.config.json`; `AGENTS.md`; `knowledge/yagni.md`; `knowledge/learning-loop.md`; governing `docs/adr/0032-first-party-plugin-assembly-boundary.md`; retained declarative-package rules from `docs/adr/0030-trusted-local-plugins-github-first.md`; `docs/tessera/r2/PLUGIN_SDK.md`; `src/Tessera.Plugin.Abstractions`; current Broker/Core/Providers source; provider and Broker tests; solution/container/deployment files; prior learning artifacts.

## Assumptions

The explicit end-to-end request authorizes this bounded local implementation and validation sequence. No UI change is required. Existing uncommitted files are authoritative. No deploy, restart, reconcile, secret access, live provider call, or external mutation is authorized.

## Blocking Questions

None.
