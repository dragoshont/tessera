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

- [ ] **0.1 ActionPlane** — `src/Tessera.Core/Policy/ActionPlane.cs`: enum
  `Unspecified|Read|Use|Manage` + `ActionPlanes.Of(verb)` (prefix before `:`) +
  `IsManageScoped`. Backward compatible (legacy `write:`/`pay:` ⇒ Unspecified).
- [ ] **0.2 RecipeTool.Plane** — add an optional `plane` field to `RecipeTool` +
  the DTO round-trip in `LoadedPolicy.cs` (like `RecipeRotation` did).
- [ ] **0.3 PDP plane enforcement** — `PolicyDecisionPoint`: a `manage:` action is
  default-deny unless a grant pattern is *manage-scoped* (a broad `*`/`use:*` never
  reaches manage); `manage:` defaults to step-up unless explicitly loosened. Config
  default `manageRequiresStepUp: true`.
- [ ] **0.4 CredentialOwner** — enum `Service|User|Dependent` (default **Service** =
  fail-safe). Add an optional `owner` to `TargetBinding` + DTO round-trip; a
  `guardian` field for dependent. Pure model + parse, no behaviour change yet.
- [ ] **0.5 Surface plane + owner in awareness DTOs** — extend `DelegationView`/
  `ModuleView`/`PortalConnection` projections + the SPA so the dashboard shows
  "use / manage" and "your account vs household key vs for <dependent>".
- [ ] **0.6 Tests** — Core: plane classification, manage default-deny + step-up,
  owner default=service, round-trips. Broker: a `manage:` call is denied/step-up via
  a real grant; awareness DTOs carry plane/owner.
- [ ] **0.7** Adversarial review + commit + push.

---

## Phase 1 — Job A live hand-off (captcha seeding)

- [x] **1.1 Broker side** — `ILiveViewWorker` seam + `WorkerLiveViewProvider`
  (Core) + `HttpLiveViewWorker` (Broker) + `LiveViewOptions` config + DI +
  fail-closed default + tests (commit `164309f`). SPA `LiveHandoffView` already
  consumes it.
- [ ] **1.2 Worker contract doc** — write the exact `POST /live-view/arm` request/
  response contract (the shape `HttpLiveViewWorker` expects) into a runbook so the
  homelab worker can implement it. *(agent can do — doc only.)*
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

- [ ] **2.1 Medical-portal HTTP recipe** (tessera repo) — author the provider
  recipe: tools for `read:appointments`/`read:specialties`/`use:book` (`use:book` =
  step-up), `upstreamBaseUrl`, cookie injection, `refreshSpec`. Recipe authoring +
  tests; **no live egress yet** (`egress.enabled` stays false). Generic example in
  the public repo; the real host stays in homelab config.
- [ ] **2.2 Tessera SessionRefresher wiring** — enable the built-but-unwired
  `SessionRefresher` as an opt-in background rotation owner (Core/Providers + tests).
  Still inert until egress on.
- [ ] **2.3 Domain MCP credential-free** (the domain-MCP repo) — drop the direct KV
  read; add a Tessera egress client; forward the user's OIDC token
  (`{{LIBRECHAT_OPENID_ACCESS_TOKEN}}`, already forwarded) as the subject_token;
  keep the domain tool ergonomics. Behind a feature flag so the live single-server
  path keeps working until proven.
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
  (commit `164309f`, 180 tests green). **Next: Phase 0 (policy primitives).**
