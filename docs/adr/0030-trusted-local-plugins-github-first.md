# ADR 0030 — Trusted-local declarative plugins and GitHub first integration

- **Status:** Accepted (2026-08-10)

R2 loads declarative, hash-verified local packages with strict manifests and known executor kinds. It does not download or execute plugin code. The first external plugin uses real GitHub REST with a user-provided fine-grained PAT in credential custody, exact `api.github.com` egress, repository allow-list, real issue list, and exact-approved issue create plus verification/reconciliation. Generic HTTP executes only manifest-declared routes; arbitrary URLs are forbidden.
