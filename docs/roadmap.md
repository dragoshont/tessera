# Tessera — Roadmap (.NET 10)

Built in phases that each leave the project coherent and honest. The guiding rule
is unchanged: **the identity primitive comes first**, because every per-identity
feature is a confused-deputy vulnerability until callers are cryptographically
verified ([ADR 0005](adr/0005-identity-first-fail-closed.md)).

> The Python spike (v0.0.2) is archived (`*.python-spike.md`). It proved the model
> and ran live read-only. The .NET 10 build replaces it; no backwards compat.

---

## Phase 0 — Design  ✅ *(this phase)*

Architecture, ADRs, and specs (you are here):
[architecture](architecture.md) · [ADRs](adr/README.md) ·
[recipes](specs/recipes.md) · [harvest drivers](specs/harvest-drivers.md).

## Phase 1 — Broker core (.NET 10)

The trustworthy foundation, offline-tested (xUnit), no I/O in `Tessera.Core`.

- `Tessera.Core`: identity & decision model (`CallerIdentity`, `EndUserAssertion`,
  `AccessRequest`, `Decision`), **tenant** model, fail-closed **Policy Decision
  Point** (default deny, exact delegation, glob-scoped actions), recipe model.
- Config + validation (rejects fail-open policy; refuses unverified callers off
  loopback).
- `tessera validate` CLI.

## Phase 2 — Identity plane (the gate)

- `Tessera.Identity`: terminate **mTLS** (Kestrel), verify **SPIFFE X.509-SVID** /
  client certs; verify signed **OIDC** end-user assertions (`aud`, `exp`, `jti`,
  issuer allow-list); **RFC 8693** token exchange.
- **Tenant derived from verified identity** (ambient, server-set).
- This is what lets the brokering endpoint stop being fail-closed.

## Phase 3 — Store + injection egress (the data plane)

- `Tessera.Stores.Abstractions` + `Tessera.Stores.AzureKeyVault`:
  `DefaultAzureCredential` (**Managed Identity / WIF — no secret**), **per-tenant
  envelope keys**, JIT read.
- `Tessera.Broker` egress via **YARP**: inject credential, **SSRF allow-list**,
  domain-pinned, return result only. RFC 8693 downscoping for OAuth upstreams;
  session injection for the un-API'd web.
- Secret-free append-only **audit**. `tessera serve` daemon (`/healthz`, metrics).

## Phase 4 — Harvest workers

- `Tessera.Workers.Abstractions`: driver contract + **mTLS capability
  registration**; broker **router** (route by capability).
- `Tessera.Workers.Browser`: wraps the Python `sessionkeeper` browser driver.
- **Topology**: co-located (batteries-included) *and* separate-deployment
  (`tessera-browser`) — identical client contract
  ([ADR 0002](adr/0002-broker-worker-topology.md)).
- `driven` egress: scoped `act()` to the worker holding the warm session, cookie
  never crossing into the broker.

## Phase 5 — Tenancy, isolation & governance

- **Dedicated-instance** tier (deployment stamp) for medical; shared+envelope-keys
  for the rest ([ADR 0004](adr/0004-tenancy-and-isolation.md)).
- **Step-up / human-in-the-loop** for high-impact actions (write / pay / book).
- Multi-consumer onboarding recipes (n8n, crawlers, CI with ephemeral per-run
  identities); lifecycle TTLs + revoke-on-offboard.
- Optional policy-as-data (OPA/Cedar) keeping the fail-closed default.

## Future drivers

- `Tessera.Workers.Android` — emulator driver for app-only / cert-pinned providers
  ([ADR 0006](adr/0006-harvest-drivers.md)), deployed as its own worker farm.
- `Tessera.Workers.Desktop` — desktop-app driver, same contract.

---

## Does Tessera need a UI?

**Not to function. Eventually a small admin UI — Phase 5+, and API-first so the UI
is a thin client, never load-bearing for a security decision.**

The data plane is headless machine-to-machine; a UI there is pure overhead. Config
is files + CLI (`tessera validate`), which suits GitOps/code review. Four genuinely
human moments benefit from a UI later, each with a no-UI starting point:

| Human moment | UI value | No-UI start |
|---|---|---|
| "A session died — log in" | health view + re-auth in place | Sev-3 alert (harvester) + Grafana from metrics |
| Step-up approval | one-tap approve/deny | push/chat notification with approve/deny links |
| Managing grants/policy | browse/edit who-can-do-what | the grants file under version control |
| Audit / "who did what as whom" | searchable timeline | structured audit log → Loki/Grafana or `jq` |

> Rule of thumb: **the broker must be fully operable headlessly.** A UI is a
> convenience over the API, never a place where a security decision secretly lives.
