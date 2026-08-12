# All-in-One Delivery Baseline

Verified 2026-08-11 against the current worktree and read-only homelab status.

## Repository

R0-R2.1 are present in one intentionally uncommitted delivery tree: canonical principals, credential custody, Evidence, Memory, Chat, Actions, Jobs, SQLite, product UI, plugins, model adapters, and security gates. The starting deployed image predates this product runtime.

## Live homelab before cutover

- `https://tessera.hont.ro` is reachable from the MacBook.
- Tessera Deployment is 1/1 on an older immutable image, has no PVC, and `/status` has no product DB, scheduler, model, plugin, or Account projections.
- LiteLLM is 1/1 and cluster-internal.
- Regina Maria MCP account A is 1/1.
- Regina Maria MCP account B is 1/1 but intentionally parked with keep-warm disabled pending the account holder's authorization.
- Flux is Ready on `main`.

No provider secret, mailbox content, medical detail, or private network address is recorded here.

## Delivery gap

The current live URL is an old broker portal, not the all-in-one product. Cutover requires a newly built Tessera image, persistent storage, model/Gmail/RM configuration, the updated RM MCP image, and human-controlled GitOps reconciliation.