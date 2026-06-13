# Tessera — Roadmap

Tessera is built in phases that each leave the project in a coherent, honest
state. The guiding rule: **the identity primitive comes first**, because every
per-identity feature is a confused-deputy vulnerability until callers can be
cryptographically verified.

---

## Phase 1 — Identity & policy core  ✅ *(v0.0.1, shipped)*

The trustworthy foundation, fully offline-tested, stdlib-only.

- `model.py` — the two-identity vocabulary (`CallerIdentity`, `EndUserAssertion`,
  `AccessRequest`, `Decision`).
- `config.py` — TOML config with safe defaults and fail-closed validation
  (refuses fail-open policy; refuses unverified callers off loopback).
- `policy.py` — the fail-closed **Policy Decision Point**: default deny, exact
  delegation matching, glob-scoped least-privilege actions.
- `tessera validate` — author a config + grants and get immediate, specific
  feedback.

## Phase 2 — The data plane (injection)

Turn the decision core into a running broker.

- **Ingress / authentication**: terminate mTLS, verify SPIFFE X.509-SVIDs and
  signed end-user (OIDC) assertions — `aud`, `exp`, `jti`, issuer allow-list.
- **Credential resolver**: pluggable backends — Vault/OpenBao and the existing
  Azure KV; OAuth Token Exchange (RFC 8693) for OAuth upstreams; **session
  injection** for the un-API'd web (consuming `sessionkeeper`'s warm sessions).
- **Egress / injection**: perform the upstream call on the caller's behalf with
  an **SSRF egress allow-list**, domain-pinned credentials, and never returning
  the secret to the caller.
- **Audit sink**: append-only, structured, non-repudiable.
- **`tessera serve`**: the daemon, with `/healthz` and metrics.

## Phase 3 — Scale, ergonomics & governance

- **Multi-consumer onboarding**: first-class recipes for n8n, crawlers, and CI
  (ephemeral per-run SPIRE identities).
- **Step-up / human-in-the-loop**: approval flow for high-impact actions
  (writes, payments, bookings) via push/chat notification.
- **Trust-domain segmentation**: isolate high-sensitivity (medical) from
  low-sensitivity (marketplace) principals.
- **Policy-as-data**: optionally back the PDP with OPA/Rego or Cedar for richer
  rules, keeping the fail-closed default.
- **Lifecycle**: TTLs and revoke-on-offboard for every identity↔session mapping.

---

## Does Tessera need a UI?

**Short answer: not to *function*. Eventually, yes — a small admin UI — but it's
deliberately Phase 3+, and the system is designed API-first so the UI is a thin
client, never a dependency.**

**The data plane needs no UI.** Brokering a call is a headless,
machine-to-machine path: a caller connects, Tessera verifies/authorizes/injects.
A human is never in that loop, so a UI there would be pure overhead. Config is
files + CLI (`tessera validate`), which is the right interface for something that
lives in GitOps and code review.

**Four genuinely human moments *do* benefit from a UI later** — but each has a
no-UI starting point so we don't block on building one:

| Human moment | UI value | No-UI starting point (P2) |
|---|---|---|
| **"A session died — please log in"** | see which accounts are healthy/stale/dead, re-auth in place | Sev-3 alert (already in `sessionkeeper`) + Grafana panel from metrics |
| **Step-up approval** for a risky action | one-tap approve/deny with context | push/chat notification with approve/deny links |
| **Managing identities, grants, policy** | browse/edit grants, see who-can-do-what | the `grants.toml` file under version control |
| **Audit / "who did what as whom"** | searchable timeline | structured audit log → Loki/Grafana or `jq` |

So the recommendation is: **lean on the existing stack first** (Grafana for
health + audit, chat/push for approvals, Git for grants), and add a focused admin
UI in Phase 3 once the data plane is real and the management surface has settled.
Designing the management API first means that UI is additive — it never becomes
load-bearing for security.

> Rule of thumb: **the broker must be fully operable headlessly.** A UI is a
> convenience over the API, never a place where a security decision secretly lives.
