# Tournament of Options

## Option A — Minimal Safe Fix

Deploy the descriptor and point native clients at the existing hostname. Rejected: setup friction, stale account truth and static Plugins remain.

## Option B — Proper Architectural Fix

Add one server setup projection/bootstrap, provider-owned readiness/catalog seams, shared strict native routing, Storybook-backed Setup/Search UI, private Cloudflare rollout and cross-client evidence. Keep public discovery metadata-only.

## Option C — Defer / Ask More

Stop at repository audit and request deployment approval. Rejected: the controlling mandate already authorizes Tessera-specific deployment.

## Decision Matrix

| Option | Canonical state | Security | Setup | Deployment truth | Scope |
|---|---|---|---|---|---|
| A | partial | acceptable | poor | partial | small |
| B | complete | strongest | simple | complete | focused product slice |
| C | unchanged | unchanged | unchanged | absent | none |

## Winner

Option B. It reuses existing abstractions/platforms and closes current evidence-backed gaps without adding an Internet code-install engine or new remote-access stack.
