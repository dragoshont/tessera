# Recommended Plan

## Summary

Implement the corrective mandate and ADR 0032 using the existing plugin abstractions and execution coordinator. Generic host code owns policy/custody/dispatch/Action/evidence; a real MCP runtime owns protocol transport/discovery/invocation; plugin mappings own stable capability identities, risk overlays, accounts, and any justified native provider behavior.

## Implementation Sequence

1. Audit all provider leakage and document reuse candidates/decisions for RM, Gmail, and GitHub, including origin, license, pin, auth/scopes, telemetry, multi-account behavior, network destinations, and rejection reasons. Review only known local integration/config locations and public sources; never read secrets. Produce the complete mandate set under `docs/tessera/integrations/`: `PROVIDER_LEAKAGE_AUDIT.md`, `INTEGRATION_REUSE_MATRIX.md`, `INTEGRATION_ARCHITECTURE.md`, `INTEGRATION_COMPATIBILITY.md`, `MCP_RUNTIME.md`, `MCP_INSTALLATION.md`, `PLUGIN_DEVELOPER_GUIDE.md`, `GMAIL_REUSE_DECISION.md`, `REGINA_MARIA_REUSE_DECISION.md`, `GITHUB_REUSE_DECISION.md`, `ACCOUNT_BINDING_MODEL.md`, `CAPABILITY_RISK_OVERLAYS.md`, `THIRD_PARTY_INTEGRATIONS.md`, `MIGRATION_REPORT.md`, `ADVERSARIAL_INTEGRATION_ARCHITECTURE_REVIEW.md`, and `ADVERSARIAL_INTEGRATION_SECURITY_REVIEW.md`. Keep these consistent with existing delivery docs rather than creating a competing master specification.
2. Implement the provider-neutral MCP runtime using the actual protocol and only required transports. For streamable HTTP/remote MCP, enforce fixed operator configuration, TLS/SSRF/redirect/size/time bounds, cancellation, server identity/version, tool discovery, schema validation, structured results, reconnect/health, and redacted observability. Add stdio only if selected reuse requires it, with explicit executable/args and a bounded environment.
3. Extend the generic plugin/module registry atomically: hash-pinned package/module join; explicit `BUILT_IN`, `TRUSTED_EXTERNAL`, `USER_APPROVED_EXTERNAL`, `UNTRUSTED`, and `DISABLED` states; account hooks; stable capability mappings; authoritative risk overlays; disable/removal/drift behavior; and generic Chat/Job definitions generated from manifests instead of provider switches. `UNTRUSTED` and `DISABLED` contribute nothing executable.
4. Build a deterministic test MCP and focused tests for read, authorized write through Tessera Action, malicious descriptions/results, timeout, cancellation, malformed/oversized data, schema/tool drift, server restart, account substitution, untrusted/hash-mismatched/downgraded module, and disable during execution.
5. Reuse the audited local Regina Maria MCP through the generic runtime, with isolated ConnectedAccount-to-endpoint/session binding and separate user/wife instances. Keep canonical proposal/preflight/read-back verification in the plugin mapping/overlay, not Broker. Preserve action-token enforcement and all existing RM behavior tests.
6. Implement the Gmail decision: use a safe self-hosted portable integration if one meets mailbox privacy, scopes, refresh, multi-account, and feature requirements; otherwise move the existing official Google API/OAuth/sync/MIME implementation unchanged into `Tessera.Plugins.Gmail`. If the chosen MCP cannot provide efficient Gmail history ingestion, keep incremental sync as a plugin-owned source-ingestion adapter while interactive tools use MCP. In every mode, Broker sees only manifests and generic hooks.
7. Implement the GitHub decision: prefer a suitable pinned official MCP mapping; otherwise extract current bounded REST behavior into `Tessera.Plugins.GitHub`. Repository IDs, issue schemas, validation, and verification remain plugin-owned. Shared HTTP transport, credential-reference resolution, Action policy, SSRF controls, and generic egress may remain provider-neutral infrastructure; GitHub request/response semantics may not.
8. Remove every provider-specific adapter, config option, account endpoint, hosted worker, health check, capability class, tool schema, dispatch switch, and evidence special case from Broker/Core/generic Providers. Generic Chat/Jobs enumerate stable mapped capabilities and accounts from the registry and handle ambiguity uniformly.
9. Add architecture and lifecycle tests: zero-plugin boot, per-plugin absence/disable/removal, direct invocation denial, dependent Job blocking, unrelated Chat/Job continuity, historical Action/Evidence survival, project/assembly/source boundaries, and replacement of RM by an equivalent test MCP without Chat/Job/Action/UI/Memory changes.
10. Package pinned first-party/external integration artifacts into the plugin discovery directory without Broker/Core project references. Validate Compose/Kubernetes plans only; any apply/restart/network/secret mutation requires approval.
11. Run focused suites after each slice, full backend/frontend/deployment gates, test-MCP restart/disable dogfood, and independent architecture/security/product judges. Fix every credible finding.
12. Continue to homelab deployment and real E2E. Discover the existing LiteLLM and deployment target safely, then pause only for Gmail OAuth, each RM account holder's login/MFA, and a specifically approved safe external side effect. Verify Chat, Memory, required Jobs, account isolation, Action approval, provider read-back, and restart/recovery.
13. Finish with the exact mandate scorecard covering provider leakage, architecture/MCP/plugin lifecycle, each provider decision/auth/E2E, homelab/LiteLLM/Chat/Jobs/Actions/recovery, adversaries, and full gates. Derive and report exactly one final status from `DELIVERED_E2E_MCP_FIRST`, `DEPLOYED_AUTH_CHECKPOINTS_REMAIN`, `DEPLOYED_EXTERNAL_BLOCKER`, `PARTIAL`, `BLOCKED`, or `FAILED`; never pre-assert success or an auth-only outcome.

## Test Strategy

- Generic MCP protocol tests immediately after the first substantive runtime edit.
- Test-MCP transport, schema, risk-overlay, account, Action, malicious-output, drift, restart, and disable tests.
- Plugin-owned behavior suites for RM/Gmail/GitHub, including current adversarial cases and unknown-outcome reconciliation.
- Broker tests for manifest-driven neutral dispatch, zero-plugin startup, direct-invocation denial, dependent Job blocking, unrelated Chat/Job continuity, and historical-state survival.
- Static project, assembly, namespace, source, and project-reference boundary tests.
- Full backend, frontend, Storybook, Playwright, Compose, backup/restore, and Kubernetes render/policy gates after stabilization.

## Rollback / Recovery

No destructive data migration is planned. Each integration can fall back to its prior external/native implementation behind the same stable plugin/capability identities. Declarative records and historical evidence remain intact. A malformed, drifting, disabled, or absent module contributes nothing atomically; unknown external outcomes are reconciled rather than blindly retried.

## Human Approval Needed

The user explicitly approves implementation, deterministic validation, documentation, local test-MCP execution, and plan-only deployment work. Cluster/GitOps apply, network changes, secret access/materialization, external OAuth/MFA, account-holder consent, runtime restarts, and real external side effects remain explicit approval checkpoints.
