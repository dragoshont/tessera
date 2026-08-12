# Tessera Security Boundaries

## Trust Plane

The current broker is preserved as Tessera's trust plane. It owns authentication, caller/end-user separation, deterministic grants, credential custody, binding resolution, constrained egress, write approval, and security audit. Kernel records cannot authenticate a caller or grant authority.

## Verified Trust Fixes

- **Named caller confirmation is blocked.** The legacy `confirmed` wire flag is not authorization; named write tools remain at step-up and do not reach upstream transport.
- **Portal credential references are server-generated.** Client-supplied store-key names are ignored; self-service connection creation allocates an opaque `tessera-ref-*` and binds it to the signed-in owner.
- **Canonical new bindings.** New portal bindings carry the `(issuer, tenant, subject)`-derived principal ID and are self-owned. An admin cannot manufacture another user's canonical binding.
- **Legacy compatibility is isolated.** Existing bindings with no canonical principal ID may still match signed subject or `preferred_username`; this is legacy policy compatibility, not the canonical path for new bindings.
- **Live resolver state.** Portal policy mutation atomically replaces the resolver's immutable binding snapshot, and refresh orchestration reads current policy.
- **Live-view message binding.** The iframe accepts messages only from the expected origin and its own `contentWindow`.

## Authorization Atomicity

`IActionAuthorizationRepository.TryConsumeAndAuthorizeAsync` defines one indivisible reservation: validate and consume the exact unexpired authorization while transitioning the matching action from `PROPOSED` to `AUTHORIZED`. The SQLite adapter performs both updates in one SQLite transaction and commits both or rolls both back.

This prevents a crash from burning approval without reserving work. It does not by itself create trusted approval; broker-integrated policy and out-of-band human issuance remain required before any live side effect.

## Data Separation

The Kernel SQLite schema has no credential, grant, binding, broker security-audit, prompt, model/worker-output, diagnostics, OAuth-token, password, API-key, or raw-credential columns. Its table set contains only Kernel state. This is structural separation, not proof that arbitrary text/JSON fields can never receive prohibited content; producer validation and dedicated content-leakage tests remain required.

- Credentials remain behind `ICredentialStore`.
- Grants and bindings remain policy configuration.
- Security decisions remain in `IAuditSink`.
- Product provenance references evidence and producers without becoming security audit.

## Fail-Closed Rules

- Missing or malformed canonical identity is rejected.
- Cross-owner repository access is rejected.
- Evidence or worker output cannot mutate policy, issue approval, or directly invoke a capability.
- Authorization mismatch, expiry, replay, stale action version, or invalid transition returns no reservation.
- Unknown capability/version is not guessed.
- Ambiguous external outcome must reconcile before live retry; R0 does not yet prove provider-specific timeout classification.

## Explicit Non-Claims

No current documentation claims production mTLS/SVID hosting, complete erasure, production backup/restore, provider integration, cloud-model safety, or live-write readiness.