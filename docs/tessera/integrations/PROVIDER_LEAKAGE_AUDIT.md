# Provider Leakage Audit

**Status:** Remediated and executable guard added 2026-08-11.

## Classification

| Provider | Current location | Classification | Required owner |
|---|---|---|---|
| Regina Maria | Former Broker capabilities/endpoints/health and generic Providers adapter | Leakage removed | `Tessera.Plugins.ReginaMaria`; protocol in `Tessera.Mcp.Client` |
| Gmail | Former Broker OAuth/workers and generic Providers REST/OAuth | Leakage removed | `Tessera.Plugins.Gmail` direct official API fallback |
| GitHub | Former Broker dispatch and generic Providers REST adapter | Leakage removed | `Tessera.Plugins.GitHub` over official GitHub MCP |
| All three | Former provider switches and schemas in `R2ProductEndpoints.cs` | Leakage removed | Manifest-driven model-tool projection and generic dispatch |
| Gmail/RM | Former provider options in Core configuration | Leakage removed | Generic host configuration parsed by each plugin |
| Gmail | Former SQLite Gmail sync records | Runtime leakage removed | Generic `plugin_cursor_states`; old names retained only in migration history |

## Generic Code That May Remain

`ICapability`, `CapabilityRegistry`, `ExecutionCoordinator`, `ConnectedAccount`, credential references, policy, Actions, Evidence, Job grants, HTTP transport, SSRF controls, result bounds, generic session refresh, and generic OAuth-MCP acquisition are provider-neutral.

Provider names in historical data, manifests, UI labels, tests, or migration documentation are not implementation leakage. Provider names in dispatch switches, schemas, configuration types, adapters, workers, or normalization code are leakage.

## Enforced Exceptions

Neutral runtime source has no provider identifiers except:

- the generic GitHub PAT detector in `ProductContentValidation`, which prevents secret persistence;
- the v13-to-v14 SQLite migration that copies and drops the historical Gmail cursor table.

`ProviderBoundaryTests` fails on any other provider identifier in neutral runtime source, concrete plugin project/assembly dependency, or provider implementation type.
