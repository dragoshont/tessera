# ADR 0003 — Pluggable credential store, Azure Key Vault default via Managed Identity / WIF

- **Status:** Accepted (2026-06-13)
- **Deciders:** maintainer (Dragoș)

## Context

The broker reads credential **bundles** (tokens / cookies) from a backing store
and (later) writes rotated material back. Different operators have different secret
backends, and the secret transit between the store and the broker is itself a
prime attack target — so it must be both **pluggable** and **secretless**.

Two axes were explicitly separated by the maintainer:

- The **harvester** is *batteries-included*, not pluggable — a user shouldn't think
  about it ([ADR 0006](0006-harvest-drivers.md)).
- The **store** *is* pluggable — people genuinely have different vaults.

## Decision

Define a single, narrow **`ICredentialStore`** abstraction (read a bundle by name;
later write a rotated bundle). Ship providers:

- **Azure Key Vault — the batteries-included default and primary focus.**
- **HashiCorp Vault / OpenBao** — opt-in.
- **Vaultwarden / Bitwarden** — opt-in, primarily for *testing* (see note).
- **In-memory / file** — for tests and local dev.

For Azure Key Vault, authenticate with **`DefaultAzureCredential`** resolving to a
**Managed Identity** or **Workload Identity Federation** — *no client secret*.
Federate the Kubernetes ServiceAccount token → Microsoft Entra → Key Vault. This
retires the long-lived `AZURE_CLIENT_SECRET` (an OWASP NHI #7 "long-lived secret"
risk) used by the Python spike.

Key Vault hardening (Microsoft Zero-Trust guidance):

- **TLS 1.2/1.3**; every request independently authenticated.
- **Key Vault firewall + Private Endpoint**; disable public network access.
- **Azure RBAC** least-privilege (not legacy access policies), scoped per tenant
  where possible; consider **one Key Vault per tenant** for strong isolation.
- **Soft-delete + purge protection** on.
- **Audit logging + Microsoft Defender for Key Vault**.

## Consequences

- **Positive:** secretless transit (nothing to leak or rotate); operators can bring
  their own vault; the default path is hardened per Microsoft guidance.
- **Negative:** Managed Identity / WIF requires Entra federation setup; not every
  environment has it.
- **Mitigation:** fall back to a **certificate credential** on an app registration
  (still better than a client secret) where WIF is unavailable.

## Note on Vaultwarden

Vaultwarden implements the **Bitwarden password-vault** API (end-to-end encrypted;
reading ciphers needs a master key/session), which is awkward for machine-to-machine
use. The clean fit is **Bitwarden Secrets Manager** (access-token based, built for
services), but Vaultwarden's Secrets-Manager support is limited/experimental. So
Vaultwarden is great as a **local test backend** (one container) but is not a
first-class production store. We will validate the exact API path empirically when
the store layer is built, not on paper.
