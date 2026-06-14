# Implementation plan — action broker (ADR 0018/0019/0020) + Job A

> Living checklist for building Tessera into the credential-backed **privileged
> action broker** ([ADR 0018](../adr/0018-access-gateway-and-action-broker.md)) with
> the actor/plane/risk split ([ADR 0019](../adr/0019-app-integrations-and-user-delegated-actions.md))
> and the credential-ownership axis ([ADR 0020](../adr/0020-credential-ownership.md)),
> plus the Job A captcha live hand-off ([ADR 0016](../adr/0016-admin-portal.md) §3).
>
> **Priority order (maintainer-approved):** Job A → Mode U → Media broker → Gmail/Graph.
>
> **Conventions for every phase:** implement for real (no stubs) → adversarial
> self-review → `dotnet test Tessera.slnx` green → PII gate → commit → push. Mark
> each task done here as it lands. A **🛑 STOP — operator** task is a security-
> sensitive shared-infra / live-egress / medical / real-credential step that needs
> the maintainer's hands + sign-off; the agent builds up to that line and stops.
>
> Build/test prefix (always): `export PATH=/usr/local/share/dotnet:$PATH && hash -r`.

---

## Phase 0 — Policy primitives (tessera repo only · no deploy risk · do first)

The shared foundation several later phases reuse. Pure code, fully testable, no
egress, no deploy. Build before the media broker; Mode U also benefits.

- [x] **0.1 ActionPlane** — `src/Tessera.Core/Policy/ActionPlane.cs`: enum
  `Unspecified|Read|Use|Manage` + `ActionPlanes.Of(verb)` (prefix before `:`) +
  `IsManageScoped` + `TokensOf` + `ToToken`. Backward compatible (legacy `write:`/`pay:` ⇒ Unspecified).
- [x] **0.2 RecipeTool.Plane** — added an optional `plane` field to `RecipeTool`
  (+ `EffectivePlane` fallback to the verb) + the DTO round-trip in `LoadedPolicy.cs`.
- [x] **0.3 PDP plane enforcement** — `Grant.MatchesAction`: a `manage:` action is
  default-deny unless a grant pattern is *manage-scoped* (a broad `*`/`use:*` never
  reaches manage); `PolicyDecisionPoint` defaults `manage:` to step-up. Config knob
  `policy.manageRequiresStepUp: true` wired through `BrokerHost`.
- [x] **0.4 CredentialOwner** — enum `Service|User|Dependent` (default **Service** =
  fail-safe) + `CredentialOwners.Parse/ToToken`. Added optional `owner` + `guardian`
  to `TargetBinding` + DTO round-trip. Pure model + parse.
- [x] **0.5 Surface plane + owner in awareness DTOs** — extended `DelegationView`/
  `ModuleView` (`Planes`) + `PortalConnection` (`Owner`/`Guardian`) projections, the
  portal HTTP DTOs, and the SPA (a shared `PlaneBadges`, an owner chip + row in the
  connection drawer, types + fixtures).
- [x] **0.6 Tests** — Core: `ActionPlaneTests`, `PlaneEnforcementTests`,
  `CredentialOwnerTests`, `PolicyRoundTripTests` (+38). Broker: delegation/module
  planes + connection owner (+3). 221 .NET green; web build + 52 tests + lint green.
- [x] **0.7** Adversarial review + commit + push.

---

## Phase 1 — Job A live hand-off (captcha seeding)

- [x] **1.1 Broker side** — `ILiveViewWorker` seam + `WorkerLiveViewProvider`
  (Core) + `HttpLiveViewWorker` (Broker) + `LiveViewOptions` config + DI +
  fail-closed default + tests (commit `164309f`). SPA `LiveHandoffView` already
  consumes it.
- [x] **1.2 Worker contract doc** — [`live-view-worker-contract.md`](live-view-worker-contract.md)
  specifies the exact `POST /live-view/arm` request/response (the shape
  `HttpLiveViewWorker` expects), the fail-closed rules, the per-principal slot
  binding + cookie-stays-in-worker invariants, and a reference worker sketch for the
  homelab noVNC pool.
- [ ] **1.3 🛑 STOP — operator: homelab noVNC worker.** The playwright-pool noVNC
  (port 6080) + CDP (9222) are **deliberately pod-internal** (cardinal invariant:
  the cookie never leaves the pod; harvester `sessionkeeper` writes worker→KV). Job
  A needs a NEW thin worker service that: mints a short-TTL, single-use, per-session
  noVNC URL; maps a person→playwright slot; arms the login; returns the contract
  response — **without** breaking the worker-trust-zone. This exposes a medical
  seeding surface and activates the staged pool → maintainer's security call + hands
  on the real reCAPTCHA. Until then Job A stays fail-closed (503) in prod.
- [ ] **1.4 🛑 STOP — operator: wire + verify** — set `liveView.enabled` +
  `workerArmUrl` in the homelab tessera config, reconcile, seed one real session
  end-to-end.

---

## Phase 2 — Mode U (medical-portal credential-free through Tessera) · MEDICAL · careful

The "one MCP, Tessera routes by identity" end-state ([ADR 0015](../adr/0015-mcp-egress-through-tessera.md)).
The medical portal (the `health-portal` example target) is `owner: user` (one
binding per person; each owns + seeds their own).

- [x] **2.1 Medical-portal HTTP recipe** (tessera repo) — the generic `health-portal`
  recipe in [`grants.example.json`](../../deploy/config/grants.example.json) +
  [`recipes.md`](recipes.md): `read:appointments`/`read:specialties`/`use:book`
  (`use:book` = step-up, `plane: use`), `upstreamBaseUrl`, `cookieMap`, `rotation:
  tessera`, `refreshSpec`. `owner: user` on the binding. No live egress (`egress`
  stays gated). Real host stays in the homelab overlay.
- [x] **2.2 Tessera SessionRefresher wiring** — `RefreshSpec` moved to Core +
  carried on `Recipe`; `SessionRefreshOrchestrator` (Providers) = the sole-owner
  pass (only `rotation.owner = tessera` + `refreshSpec` recipes); `SessionRefreshService`
  (Broker `BackgroundService`); `RefreshOptions` (off by default, validation ties it
  to `egress.enabled`, host registers it only when enabled + the store can write).
  +10 tests (orchestrator, config, round-trip). Inert until egress on.
- [ ] **2.3 🛑 STOP — operator (cross-repo): domain MCP credential-free** (the
  domain-MCP repo, not tessera) — drop the direct KV read; add a Tessera egress
  client; forward the user's OIDC token as the subject_token; behind a feature flag
  so the live single-server path keeps working until proven. *(Lives in the medical
  MCP repo + touches live medical access → operator.)*
- [ ] **2.4 🛑 STOP — operator: cutover** — homelab: add the portal host to
  `egress.allowedHosts`, `egress.enabled=true`, point the MCP at Tessera, make
  Tessera the **sole** rotation owner (retire per-person keep-warm in the SAME
  commit), verify both identities end-to-end. Medical data + live booking surface →
  maintainer sign-off.

---

## Phase 3 — Media action broker (Seerr / Sonarr / Radarr / qBittorrent)

User-delegated actions on `owner: service` keys ([ADR 0019](../adr/0019-app-integrations-and-user-delegated-actions.md)).
Needs **Phase 0** (planes + owner). Lower risk than Mode U (own homelab apps, stable
API keys), but flipping egress live with real keys is operator.

- [ ] **3.1 Recipes** (tessera repo) — `seerr`, `sonarr`, `radarr`, `qbittorrent`
  recipes with read/use/manage tools (e.g. `seerr_search`=read, `seerr_request`=use,
  `seerr_approve`=use+step-up+admin, `seerr_settings`=manage+step-up). `owner:
  service`. Recipe + tests; no live egress.
- [ ] **3.2 Grants** — example grants over the planes (member: read+use; operator:
  +manage, all step-up). Tests proving a member can't reach `manage:`.
- [ ] **3.3 MCP exposure** — confirm `tessera_call` surfaces these tools with the
  plane/step-up metadata; the chat can call read/use, manage is step-up-gated.
- [ ] **3.4 🛑 STOP — operator: credentials + egress** — store the real API keys in
  KV, add bindings (`onBehalfOf: null`), add hosts to `egress.allowedHosts`,
  `egress.enabled=true`, wire into chat. Real keys + live egress → operator.
- [ ] **3.5 (optional) Home Assistant** — curate pre-classified HA tools
  (`ha_light_on`=use:light, `ha_lock_unlock`=use:lock+step-up,
  `ha_automation_create`=manage+step-up). `owner: service`. Same STOP for the token.

---

## Phase 4 — Gmail / email + Microsoft Graph · personal data · `owner: user`

Tier A (Graph official OAuth) + Tier B (IMAP/SMTP). Metadata-first, draft/confirm
writes, result classes (metadata→preview→full-body). All `owner: user`.

- [ ] **4.1 Result-class primitive** — implement the metadata/preview/full-body/
  attachment/receipt output classes from the spec (Core + tests); search returns
  opaque handles, full body by handle only.
- [ ] **4.2 Microsoft Graph calendar-read** — recipe + delegated-scope consent
  receipt; `read:calendar` first. Separate Graph grant from the login token (spec).
- [ ] **4.3 Gmail** — decide Graph-mail vs IMAP/SMTP (Tier B); `read:mail` metadata
  first, `use:send` = draft→confirm→step-up.
- [ ] **4.4 🛑 STOP — operator: consent + credentials** — per-data-class consent,
  app-specific passwords / delegated tokens into KV, live egress. Operator.

---

## Cross-cutting (thread through, not a final bolt-on)

- [ ] Consent receipts (per ownership mode) — [ADR 0020](../adr/0020-credential-ownership.md).
- [ ] Reveal path for `owner: user` (owner-only · step-up · auto-redact · audit ·
  never to an agent) — **only if needed**; off by default.
- [ ] Dependent/guardian relationship model (guardian may seed + act-as a named
  dependent) — minimal v1.
- [ ] Step-up / sudo re-auth UX in the portal (ADR 0016 cross-cutting) — gates the
  admin surface + every `manage:` / destructive / medical action.

---

## Status log

- 2026-06-14: docs committed — ADR 0018/0019 (+ read/use/manage planes), ADR 0020
  (credential ownership), service-access spec, this plan. Job A broker side done
  (commit `164309f`, 180 tests green).
- 2026-06-14 (overnight): **Phase 0 complete** — ActionPlane + CredentialOwner +
  PDP manage default-deny/step-up + awareness DTO/SPA surfacing + tests (221 .NET
  green, web build + 52 + lint green).
- 2026-06-14 (overnight): **Phase 1.2 complete** — live-view worker contract doc.
- 2026-06-14 (overnight): **Phase 2.1/2.2 complete** (broker side) — medical recipe
  example + recipes spec, RefreshSpec on Recipe, SessionRefreshOrchestrator +
  SessionRefreshService + RefreshOptions (off by default, inert until egress).
  230 .NET green. 2.3 (domain MCP, cross-repo) + 2.4 (cutover) stay operator.
  **Next: Phase 3 (media action broker).**
