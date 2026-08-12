# Intake

## Understanding

Replace provider-aware Broker/Core/runtime composition with the accepted ADR 0032 first-party plugin boundary. First complete reuse discovery and a real provider-neutral MCP protocol runtime, then reuse or extract Regina Maria, Gmail, and GitHub without losing current behavior. Continue through deployment and real E2E, pausing only for external identity/consent or safe side-effect approval.

## Acceptance Criteria

1. Reuse discovery is completed and documented for Regina Maria, Gmail, and GitHub before retaining or adding native provider code.
2. A real provider-neutral MCP runtime uses the actual protocol for required streamable HTTP/remote transport and supports lifecycle, health, discovery, schema validation, invocation, cancellation, bounds, and generic errors. Stdio is added only if a selected integration needs it.
3. MCP tools enter Tessera only through stable capability manifests plus authoritative risk overlays. Unknown or changed write semantics fail closed and never auto-enable.
4. Broker/Core have no compile dependency on `Tessera.Plugins.*` and contain no provider implementation IDs, account schemas, normalization, tool construction, OAuth behavior, health workers, or provider configuration types.
5. Broker resolves credential references, authorizes egress, dispatches capabilities, enforces policy/approvals, and records execution/evidence. ConnectedAccount, Policy, Action, Job, Evidence, and Memory remain Tessera-owned.
6. Regina Maria, Gmail, and GitHub implementations and provider tests move behind plugin contracts. RM reuses the audited local MCP if suitable; Gmail/GitHub follow their documented decisions.
7. Plugin absence, disable, removal, downgrade, hash mismatch, schema/tool drift, and disable-during-execution fail closed without taking Tessera down or deleting historical state.
8. A deterministic test MCP proves read, Action-wrapped write, malicious output, timeout, schema drift, restart, account binding, and replacement of the RM implementation without changing Chat, Jobs, Actions, Accounts UI, or Memory schema.
9. Exact approval remains bound to principal, account, plugin/version, capability, external tool, normalized payload, target, expiry, and single use. Unknown outcomes reconcile before any retry.
10. Existing provider behavior remains covered, including Gmail OAuth/sync/MIME constraints, GitHub repository bounds, and RM canonical slot receipts, booking, reschedule, cancellation, account isolation, and provider verification.
11. Architecture tests enforce project, assembly, namespace, and source boundaries. Focused tests run after the first runtime edit; full backend gates and independent judges run when stable.
12. Broker starts with zero provider plugins; disabling/removing RM, Gmail, or GitHub removes only dependent capabilities and blocks dependent Jobs while unrelated Chat/Jobs and historical Evidence remain usable.
13. Packaging deploys optional pinned integrations without introducing Core/Broker project references. Infrastructure apply, secrets, OAuth/MFA, and real side effects remain explicit human checkpoints.
14. Final delivery proves the corrected architecture, homelab runtime, existing LiteLLM, real Chat/Memory/Jobs, Gmail, two isolated RM accounts, approvals, verification, restart/recovery, and adversarial gates.

## Grounding Sources

`AGENTS.md`; `architrave.config.json`; `docs/adr/0032-first-party-plugin-assembly-boundary.md`; `docs/tessera/r2/PLUGIN_SDK.md`; `docs/tessera/r2/CAPABILITY_RUNTIME.md`; `knowledge/backend.md`; `knowledge/yagni.md`; `gates/rubric.md`; `/Users/dragoshont/Downloads/TESSERA_MCP_FIRST_INTEGRATION_CORRECTION_AND_DELIVERY_MANDATE.md` (controlling); `/Users/dragoshont/Downloads/TESSERA_ALL_IN_ONE_REAL_WORLD_DELIVERY_SPEC.md` (delivery requirements not superseded); current source, tests, project graph, and dirty diff.

## Assumptions

- Current dirty changes are authoritative and must be preserved unless they directly violate this boundary.
- Preserve functionality means preserving current contracts and observable behavior while changing ownership and composition.
- No schema migration, deploy, external authorization, secret access, or runtime mutation is required.

## Blocking Questions

None. The user explicitly authorized implementation of this boundary correction and autonomous continuation through its validation phases.
