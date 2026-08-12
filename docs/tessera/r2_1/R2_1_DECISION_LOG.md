# R2.1 Decision Log

## Host Devloop Is Authoritative

**Decision:** `scripts/devloop/up` is the zero-config dogfood path; Compose remains OIDC-oriented.
**Reason:** loopback dev sign-in is safe only on loopback.
**Alternatives:** weaken Compose auth; require Kubernetes. Rejected.
**Evidence:** clean runtime and `/readyz` proof.
**Scope:** local Alpha. **Reversible:** yes. **Revisit:** production auth bundle exists.

## Readiness Separates Operation From Configuration

**Decision:** database/scheduler gate readiness; model/plugin/Account states remain visible configuration/degradation components.
**Reason:** a clean install is operable before external setup.
**Alternatives:** mark all clean installs unready. Rejected.
**Revisit:** orchestration platform requires stricter dependency readiness.

## Stream Deltas Are Transient

**Decision:** owner/conversation/execution-bound, bounded in-memory `text` SSE events; final validated message is canonical.
**Reason:** first-token UX without persisting unvalidated fragments.
**Alternatives:** persist every token; buffer all output. Rejected.
**Revisit:** cross-node streaming or resumable provider protocol is required.

## Provider Identity And Permissions Are Separate

**Decision:** store stable provider ID/login, raw provider scopes, canonical Tessera permissions, and capability bindings separately. Fine-grained GitHub read is repository-probed; write is never inferred.
**Reason:** provider scope vocabularies differ and fine-grained tokens omit OAuth headers.
**Revisit:** GitHub exposes a reliable repository-permission endpoint needed for write proof.

## Verified Atomic Restore Only

**Decision:** online backup/restore uses temporary SQLite image, integrity check, atomic rename, no overwrite. Active replacement is offline/manual.
**Reason:** avoid deleting active or valid destination state.
**Revisit:** transactional operator restore workflow is added.

## Model Egress Is Public Or Loopback

**Decision:** remote model endpoints cannot resolve to private/internal ranges; explicit loopback local adapters remain supported.
**Reason:** no model host allow-list existed to justify private egress.
**Revisit:** an explicit approved private-endpoint policy is designed.

## One Active Scheduler

**Decision:** document single-active-scheduler topology; keep durable lease/fence recovery.
**Reason:** do not overclaim multi-replica execution.
**Revisit:** deployment requires horizontal scheduler replicas.