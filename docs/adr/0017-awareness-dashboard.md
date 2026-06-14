# ADR 0017 — Awareness dashboard (read-only transparency surface)

- **Status:** Accepted (2026-06-14)
- **Deciders:** maintainer (Dragoș)
- **Relates to:** [ADR 0016](0016-admin-portal.md) (admin portal — the surface this
  extends), [ADR 0008](0008-policy-and-identity-administration.md) (file-first,
  headless-first), [ADR 0005](0005-identity-first-fail-closed.md) (verified identity,
  fail-closed), [ADR 0014](0014-http-injectable-provider-egress.md) (egress + the
  refresher that would feed real schedule data), [ADR 0015](0015-mcp-egress-through-tessera.md)
  (the Mode U end-state this dashboard makes legible), [ADR 0004](0004-tenancy-and-isolation.md)
  (medical isolation).
- **Detailed design:** [specs/admin-portal.md](../specs/admin-portal.md) §12 (Awareness dashboard).

## Context

[ADR 0016](0016-admin-portal.md) gave the portal three surfaces — **live hand-off**,
**connect wizard**, **accounts inventory** — built in value-per-effort order. Job A
(the captcha hand-off) is the crown jewel but depends on a browser worker the
roadmap defers ([spec R1](../specs/admin-portal.md#7a-known-risks--open-questions-self-review)).

The maintainer asked a sharper, **orthogonal** question:

> *When I sign in, I want to get a good sense — at a glance and in detail — of
> **who is using auth, what modules are loaded, who/what can act as me, whether an
> automatic refresh job runs, and a log of what happened** (a terminal-like stream
> and a human summary). Make it easy for a user **and** for me as admin.*

None of that is the captcha hand-off. It is a **transparency / awareness** layer:
*"show me, secret-free, the state of my delegated authority and its use."* Crucially,
**all of it is a projection over data the broker already produces** — grants,
bindings, recipes, and the append-only secret-free audit
([ADR 0008](0008-policy-and-identity-administration.md), already implemented as
[`AuditEntry`](../../src/Tessera.Core/Audit/IAuditSink.cs) +
[`JsonlAuditSink`](../../src/Tessera.Broker/JsonlAuditSink.cs)). It needs **no new
trust root, no mutation, no upstream egress, and no database** (spec §7b) — it is
the same headless-first projection the portal already is, widened from *"what is
connected"* to *"who may act as me, and what have they done."*

This matters independently of Job A: even with **zero** workers wired, an awareness
dashboard answers the questions a household member and an operator actually ask
day-to-day, and it makes the [ADR 0015](0015-mcp-egress-through-tessera.md) **Mode U**
end-state *legible before it is built* (you can see the delegations and the audit
that Mode U will route through Tessera).

## Decision

**Add a read-only, secret-free Awareness dashboard to the portal: four additive
projections over existing data, surfaced as a self "Activity & Access" view and an
admin "Observability" view. It mutates nothing, reveals no secret value, and adds
no datastore.** It slots into [ADR 0016](0016-admin-portal.md) as **Phase 0+** —
after the read-model + auth (Phase 0), independent of and concurrent with Job A.

The four projections (each a new `GET` under `/portal`, each authorized exactly like
the existing surface — self by default, cross-person operator-only):

| # | Endpoint | Projects | Answers |
|---|---|---|---|
| **1** | `GET /portal/audit` | the broker's append-only `AuditEntry` stream (a bounded in-memory tail tee'd off the existing sink) | *"a log of what happened"* — terminal stream **and** summary |
| **2** | `GET /portal/delegations` | `grants[]` keyed by `onBehalfOf` | *"who/what may act as me, on what, with which actions, and what needs step-up"* |
| **3** | `GET /portal/modules` | `recipes[]` + egress posture + binding counts | *"what modules/connectors are loaded (RM, …) and what can they do"* |
| **4** | `GET /portal/connections/{id}/schedule` | the recipe's refresh posture + the rotation owner | *"is an automatic job running for this session, and who owns rotation"* |

**Design invariants (inherited, non-negotiable):**

1. **Read-only.** No projection writes policy or store. The dashboard is a *window*,
   not a second brain ([ADR 0008](0008-policy-and-identity-administration.md)).
2. **Secret-free.** No projection returns a secret value, ever — only ids, enums,
   counts, timestamps, and presence flags. The audit spine is already secret-free by
   construction; the new endpoints preserve that and are tested for it (a
   value-leak assertion per endpoint).
3. **Fail-closed authorization.** Every endpoint resolves the caller's principal
   from a verified forwarded OIDC token (or, on loopback dev, the dev header), and
   scopes to **self** unless the caller is in `portal.admins`. A member can never
   read another person's audit, delegations, or schedule.
4. **No database.** The audit tail is a bounded in-memory ring (a fixed-size buffer,
   newest-wins); everything else is a pure projection refreshed on read. Survival
   across restarts is **not** a goal for the tail — the durable record remains the
   JSONL audit sink (stdout/file), which the ring only mirrors.
5. **Honest about thin data.** Where the underlying datum is weak today (session
   expiry has no readable TTL; rotation is owned by an *external* MCP in Mode P, not
   Tessera), the projection says so explicitly (`expiryIsEstimated`, `rotationOwner:
   "external" | "none" | "tessera"`) rather than inventing a number. Mode U
   ([ADR 0015](0015-mcp-egress-through-tessera.md)) is what later makes rotation
   `"tessera"` and the schedule real.

**Authentication** reuses the identity plane already required — the same verified
principal that grants/bindings key on ([ADR 0011](0011-identity-provider-sso.md)).
No new auth, no local password.

## Self-review (adversarial analysis of *this* plan)

A skeptical reviewer's objections, and the resolution baked into the design. None
reshapes the model; each is a sequencing or scoping decision pinned here.

| # | Objection | Why it bites | Resolution |
|---|---|---|---|
| **Q1** | **An audit endpoint is a new read path to sensitive metadata** (who accessed what medical provider, when). A bug could leak one person's activity to another. | Audit rows name `onBehalfOf` + `target`; cross-person exposure is a privacy breach even though no secret value is present. | **Self-scope by default, operator-only for cross-person**, identical to `/portal/connections`. The filter is applied **server-side in the projection** (not the client), and a test asserts a member's `/portal/audit` contains only their own rows. Medical targets inherit the stricter posture ([ADR 0004](0004-tenancy-and-isolation.md)). |
| **Q2** | **In-memory ring buffer = unbounded memory / lost-on-restart.** | A naive list grows without bound; a restart drops the tail. | **Fixed-capacity ring** (default 1000, configurable), newest-wins, O(1) insert, bounded memory. Lost-on-restart is **acceptable and documented**: the JSONL sink (stdout → Loki) is the durable record; the ring is a convenience tail for the live UI. The ring never becomes the source of truth. |
| **Q3** | **Tee'ing the audit sink risks dropping or reordering the durable JSONL** if the decorator is buggy or throws. | The audit log is a security artifact; the dashboard must not weaken it. | The ring is a **decorator that calls the inner sink first**, then records to the ring inside a try/catch that can never propagate (a ring failure must not fail a decision). Order is preserved (inner write happens before ring add). A test asserts the inner sink still receives every entry when the ring is in front. |
| **Q4** | **`/portal/audit` could become a DoS amplifier** (huge responses, expensive scans). | A member or a script could request a giant window. | The ring is **inherently bounded** (≤ capacity); the endpoint caps `limit` (≤ capacity) and returns newest-first. No disk scan, no unbounded query — the response is O(capacity) worst case. |
| **Q5** | **Delegations/modules duplicate `grants.json`** — why not just read the file? | If the projection diverges from the loaded policy, the UI lies. | The projection reads the **same in-memory `LoadedPolicy`** the PDP decides on (not the file on disk), so it cannot diverge from what the broker actually enforces. It is the *enforced* policy, rendered. |
| **Q6** | **"Schedule" implies Tessera runs a job it does not** (rotation is owned by an external domain MCP today, Mode P). | Claiming a Tessera-owned schedule would be a lie that erodes trust. | The schedule projection reports `rotationOwner` honestly: `"none"` (no refresh spec), `"external"` (a domain MCP owns it — today's medical-portal MCP), or `"tessera"` (the Mode U / refresher end-state). It states *who* rotates, and only claims a Tessera schedule when the refresher is actually wired. |
| **Q7** | **Scope creep: this is a metrics/observability product**, not a credential broker. | Building dashboards can balloon. | Hard scope: **four read endpoints + two SPA views**, all projections over existing data, no new storage, no new mutation, no charts/alerting. Anything beyond (alerting, retention, export) is explicitly out and would need its own ADR. |
| **Q8** | **Admin "see everyone's activity" is a god-view** that itself needs auditing. | An operator reading members' activity is a sensitive action. | The admin observability reads are themselves **subject to the same audit** (a portal read that crosses persons is recorded), and the admin surface stays a **separate route** gated by the allow-list, consistent with [ADR 0016](0016-admin-portal.md) §6. (Step-up for the admin surface is tracked in 0016; this ADR does not weaken it.) |
| **Q9** | **Adding a Gmail/Google module** (the maintainer floated it) **mixes "awareness" with "new connector".** | Conflating the two would bloat this phase. | **Out of scope for 0017.** Adding a module is a recipe (+ a consumer) and rides the existing connect path; it is sequenced *after* the dashboard and Mode U, and the dashboard will simply *show* it once it exists. Noted in the roadmap, not built here. |
| **Q10** | **The dashboard makes Mode P's split-credential reality visible** — could look like a regression. | Showing "rotation owner: external" exposes that Tessera is not yet the custodian. | That visibility is **the point** (transparency), and it is the honest precursor to Mode U. The dashboard is what lets the operator *watch* the Mode U cutover land (rotationOwner flips external → tessera). |

## Consequences

**Positive**

- **Answers the maintainer's actual question** — who's using auth, what's loaded,
  who can act as me, is a job running, what happened — at a glance and in a
  terminal-like detail feed, for both a member (self) and an operator (all).
- **Zero new trust root.** Projections over the enforced policy + the existing
  secret-free audit; no mutation, no egress, no datastore.
- **Makes Mode U legible before it ships.** Delegations + audit + module posture are
  exactly the planes Mode U ([ADR 0015](0015-mcp-egress-through-tessera.md)) will
  route through Tessera; the dashboard lets you see them now and watch the cutover.
- **Independent of the deferred worker.** Unlike Job A, it needs no browser worker —
  it ships value with the cluster exactly as it is.

**Negative**

- **New read surface = new attack surface.** Mitigated by self-scope-by-default,
  operator-only cross-person, secret-free-by-construction (tested), and the same
  default-deny network posture + OIDC gate as the rest of the portal.
- **The audit ring is volatile.** A restart drops the live tail. Accepted: the JSONL
  sink (→ Loki) is the durable record; the ring is a UI convenience, never truth.
- **More UI to maintain.** Two new SPA views. Mitigated by reusing the established
  stack (React + shadcn/ui + Tailwind + Storybook + Playwright).

## Rejected alternatives

- **Surface the audit only via Loki/Grafana, no in-portal view.** Rejected: an
  operator-grade log explorer is great for the operator, but a *household member*
  should see their **own** activity in plain language without a Grafana account or a
  PromQL/LogQL lesson. The portal is the humane surface; Loki remains the durable
  store and the power-user explorer.
- **Persist the audit tail in a database for cross-restart history.** Rejected for
  now (spec §7b): the durable record already exists (JSONL → Loki). A DB only earns
  its keep at retention/scale needs that aren't here; adding one now is the very
  "second source of truth" [ADR 0008](0008-policy-and-identity-administration.md)
  warns against.
- **Build the schedule view on a Tessera-owned refresher first.** Rejected as a
  sequencing inversion: wiring the refresher (Mode U) is the big, medical-careful
  build; the dashboard ships *first* and honestly reports `rotationOwner: external`
  until the refresher lands, rather than blocking transparency on it.
- **One combined view with a role flag** (members and admins see the same page,
  fields toggled). Rejected, consistent with [ADR 0016](0016-admin-portal.md) §2:
  two **separate routes** (self vs operator) is clearer and safer than a single view
  that conditionally renders other people's data.
