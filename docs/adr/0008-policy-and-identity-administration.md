# ADR 0008 — Policy, grants & identity administration

- **Status:** Accepted (2026-06-13)
- **Deciders:** maintainer (Dragoș)
- **Relates to:** [ADR 0005](0005-identity-first-fail-closed.md) (verified identity),
  [ADR 0004](0004-tenancy-and-isolation.md) (tenancy)

## Context

Three kinds of configuration decide *who may do what*:

1. **Trust config** — which workload CA(s) and which OIDC issuer(s) Tessera trusts
   (i.e. *which badges are genuine*).
2. **Grants** — `(caller, on_behalf_of, target, actions)` rules (*who may do what*).
3. **Bindings** — `(target, person) → store-secret name` (*which stored secret
   backs this*).

The load-bearing question: **where does this mapping live, and how is it changed?**
A consumer (chat / MCP / CLI) must carry **none** of it — it only proves identity
([ADR 0005](0005-identity-first-fail-closed.md)). All authorization lives in the
**broker's control plane**, in one place the operator controls. The remaining
choice is the *medium* and *change process*.

## Decision

**File-first, GitOps as the source of truth.** Trust config, grants, and bindings
are declarative files loaded by the broker:

- **Declarative + versioned.** Every authorization change is a reviewable **diff**
  in version control — never a hidden click. This matches the auditability goal
  ([ADR 0001](0001-language-and-runtime.md)) and how the spike already worked
  (`grants.toml`).
- **Validated + fail-closed.** The broker validates files on load and refuses to
  start on a fail-open policy or an unverified-caller-on-a-network-address config.
  No match → deny.
- **Hot-reload** on change (watched file / signal), so updates apply without a
  restart, but every applied version is recorded for audit.
- **Designed-in seam for richer backends.** The loader sits behind an interface so
  the *same* model can later be served from a database, a small admin API, or a
  policy engine (OPA/Cedar) — **without** changing the model or the broker. The
  file remains the floor and the default.

Secrets are **never** in these files — bindings reference store-secret *names*;
the values live in the credential store ([ADR 0003](0003-credential-store-pluggable.md)).

## Consequences

- **Positive:** one auditable place for all authorization; every change is a
  reviewable, revertible diff; trivial to back up and reason about; no database to
  run for the common case.
- **Positive:** an admin UI/API, if built, is a thin layer over the same model — it
  is a *convenience over the API*, never where a security decision secretly lives
  (the headless-first rule).
- **Negative:** files don't scale to thousands of tenants or self-service editing.
- **Mitigation:** the designed-in backend seam (DB / admin-API / OPA) absorbs that
  when/if it's needed; nothing about the model changes.

## Rejected alternatives

- **Mapping inside each consumer** (e.g. the MCP decides what it may do) — rejected:
  that is the baked-in-credential antipattern; authorization must be central.
- **Database/admin-UI as the *only* source of truth from day one** — rejected as the
  default: a clickable store hides security changes and complicates audit/backup;
  keep the reviewable file as the floor, add a UI as an optional layer.
