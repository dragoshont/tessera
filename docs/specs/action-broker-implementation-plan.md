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

- [x] **3.1 Recipes** (tessera repo) — [`grants.media.example.json`](../../deploy/config/grants.media.example.json):
  `seerr`/`sonarr`/`radarr`/`qbittorrent` recipes with read/use/manage tools
  (`seerr_search`=read, `seerr_request`=use, `seerr_approve`=use+step-up,
  `seerr_settings`=manage+step-up, `qbt_delete`=use+step-up). `owner: service` on
  every binding. No live egress.
- [x] **3.2 Grants** — member (`bob`): read+use; operator (`alice`): +manage +
  approve/delete, all step-up. The `CredentialResolver` now falls back to the shared
  service key for a delegated call with no per-person binding (ADR 0020), exact
  per-person match always winning. Tests prove a member can't reach `manage:` and a
  delegated call resolves the shared key (loaded from the real file).
- [x] **3.3 MCP exposure** — `ProviderToolInfo` + `tessera_list_provider_tools` now
  carry each tool's `plane` (read/use/manage); the chat can tell the planes apart and
  which calls step up. Broker test over the real gateway.
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

- [x] **4.1 Result-class primitive** — [`ResultEnvelope.cs`](../../src/Tessera.Core/Results/ResultEnvelope.cs):
  `ResultClass` (metadata/preview/full-body/attachment/receipt), `ResultHandle`
  (opaque, target-scoped, rejects cross-provider replay), `MetadataItem`,
  `MutationReceipt`. Search returns metadata + handles; full body by handle only; a
  write returns a receipt, never a body. +8 Core tests.
- [x] **4.2 Microsoft Graph calendar-read** — `graph-calendar` recipe
  ([`grants.connectors.example.json`](../../deploy/config/grants.connectors.example.json)):
  `read:calendar` (Calendars.ReadBasic), separate target/consent from mail,
  Tessera-owned token refresh. Tested: calendar consent can't reach mail.
- [x] **4.3 Gmail + Graph mail** — `gmail` + `graph-mail` recipes: `read:mail.metadata`
  (search → handles), `read:mail.body` (by handle), `use:mail.send` (draft→confirm→
  step-up). All `owner: user`. Tested over the real file (metadata-first, send steps
  up, every binding user-owned).
- [ ] **4.4 🛑 STOP — operator: consent + credentials** — per-data-class consent,
  app-specific passwords / delegated tokens into KV, live egress. Operator.

---

## Phase C — caller authentication plane (ADR 0021) + domain-MCP cutover

> Closes the verified gap: `/v1/broker` was fail-closed (503) with no caller
> authenticator, and `/mcp` hardcodes one chat caller — so no non-chat workload
> could authenticate as a distinct verified caller. This builds the door that
> unblocks the ADR 0015 domain-MCP credential cutover. Design + adversarial Q3
> verification + use-case scoping: [caller-plane-and-mcp-cutover.md](caller-plane-and-mcp-cutover.md).

- [x] **C1.1 Caller plane core** — `CallerBrokerService` (Broker): authenticate a
  caller from its **app-only** token (reusing `ToCallerIdentity` → verified
  `OidcJwt` caller), plus an OPTIONAL forwarded end-user token
  (`X-Tessera-On-Behalf-Of`, Mode U) → verified `EndUserAssertion`; Mode P (no
  end-user) acts under the caller's own service grant. Fail-closed: a missing /
  invalid / user-token-as-caller is refused; an app-only on-behalf-of is refused.
- [x] **C1.2 Endpoint** — `POST /v1/broker` (`CallerBrokerEndpoint`) wired to the
  service with `op = call | list-tools | check`; **two fail-closed gates** (a caller
  authenticator configured **and** `egress.enabled`); `/status` now reports the
  endpoint state. The hardcoded 503 is gone (the handler fail-closes when no
  authenticator).
- [x] **C1.3 Audit fix** — `ProviderEgress` now authorization-audits **every**
  brokered call (it didn't before — the MCP path was unaudited too); wired through
  `BrokerProviderGateway.Build`. So both the MCP surface and the caller plane record
  a secret-free decision per call.
- [x] **C1.4 Tests** — `CallerBrokerServiceTests` (+16): auth branches (Mode P/U,
  user-as-caller rejected, missing/invalid token, app-only on-behalf-of rejected,
  validator-not-configured), dispatch (list-tools, read, write step-up→confirm,
  check allow, ungranted deny, Mode P service-binding), the egress-disabled gate, and
  the audit. **302 .NET green.**
- [x] **C1.5 Onboarding** — [connect-a-domain-mcp.md](../connect-a-domain-mcp.md)
  (the `/v1/broker` contract + recipe/binding/grant authoring + the gates); linked
  from getting-started + the README non-human-caller section. ADR 0021 written +
  indexed.
- [x] **C2 Domain-MCP egress client** (cross-repo, `dragoshont/homelab_mcp`) — a
  credential-free `TesseraHttpClient` (same `.get/.post` surface, routes via
  `op=invoke`) + a per-service direct-vs-broker factory switch (default off). Drops
  the upstream secrets for an opted-in service; keeps tool ergonomics (ADR 0015 shape
  C). SSH-backed + device-paired tools untouched. +17 py tests.
- [ ] **C3 🛑 STOP — operator: cutover.** Real recipes/grants/bindings (`owner:
  service`) for the actual providers in the private overlay; move the keys into
  Tessera's store; `egress.enabled` + SSRF allow-list + NetworkPolicy. Real secrets +
  live egress. **Runbook written:** `dragoshont/homelab`
  `docs/runbooks/tessera-media-broker-cutover.md`.
- [ ] **C4 (future) mTLS caller plane** (ADR 0021 phase 2) — a client cert at the
  ingress → `VerificationMethod.Mtls` caller; slots behind the same `/v1/broker`, no
  PDP/egress change.

---

## Cross-cutting (thread through, not a final bolt-on)

- [x] Consent receipts (per ownership mode) — `ConsentReceipt` (Core): per
  `(principal, target, data class)`, calendar consent never satisfies mail. +3 tests.
- [ ] Reveal path for `owner: user` (owner-only · step-up · auto-redact · audit ·
  never to an agent) — **deliberately not built**, off by default; add only if needed.
- [x] Dependent/guardian relationship model — `GuardianRelationships` (Core): a
  guardian who seeded an `owner: dependent` binding may act-as that dependent;
  derived from bindings, no new store. +4 tests.
- [ ] **🛑 operator: step-up / sudo re-auth UX** in the portal + chat (ADR 0016
  cross-cutting) — the PDP already returns `StepUp`; the UI re-auth prompt is the one
  remaining build before destructive / medical / `manage:` actions are click-safe.

> **Operator cutovers:** every 🛑 step above + each phase's `3.4`/`2.4`/etc. live in
> the [operator cutover checklist](operator-cutover-checklist.md) — the morning to-do.

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
- 2026-06-14 (overnight): **Phase 3.1-3.3 complete** — media broker recipes/grants
  example (seerr/sonarr/radarr/qbt, owner: service, read/use/manage + step-up),
  CredentialResolver service-key fallback (ADR 0020), MCP tool plane metadata.
  241 .NET green. 3.4 (real keys + egress) stays operator.
- 2026-06-14 (overnight): **Phase 4.1-4.3 complete** — result-class primitive
  (ResultEnvelope/ResultHandle/MutationReceipt), Gmail + Graph mail/calendar
  connectors example (owner: user, metadata-first, send step-up, separate consent).
  257 .NET green. 4.4 (consent + real tokens + egress) stays operator.
- 2026-06-14 (overnight): **Cross-cutting** — ConsentReceipt + GuardianRelationships
  (Core, +7 tests, 264 .NET green); [operator cutover checklist](operator-cutover-checklist.md)
  written. **All autonomous tessera-repo work is done.** Remaining = operator
  cutovers (real secrets / live egress / medical / noVNC worker) + the step-up UX
  build, all captured in the checklist.
- 2026-06-15: **Adversarial hardening pass (F1–F10)** — turned "modelled but
  unwired" into enforced behaviour and fixed real gaps found by self-review:
  - F1: result classes are now **enforced** in `ProviderEgress` — `{handle}`/
    `{placeholder}` path templating, full-body/attachment require a target-scoped
    handle (no bulk spill), metadata capped, `BadRequest` status.
  - F3: OAuth refresh uses an absolute `RefreshSpec.TokenUrl` + is SSRF-guarded
    (cookie-portal refresh on the base URL still works).
  - F5: removed the display-only plane override — plane always derives from the
    enforced verb, so the surface can't lie.
  - F4: per-grant `ManageStepUpExempt` escape hatch (loosen one named `manage:`
    action without flipping the plane).
  - F6: Mode U `refresh.acknowledgeSingleWriter` guard (rotation arms only on an
    explicit single-replica ack).
  - F7: the refresher reads the **live** policy each pass (no stale snapshot).
  - F8: delegations surface the backing credential **owner** (shared service keys
    are visible in "who can act as me").
  - F2: consent + guardian wired to real endpoints (`/portal/consents`,
    `/portal/dependents`); portal-added connections default to `owner: user`.
  - F10: e2e test proves the shared-key fallback is PDP-gated (granted user reaches
    it; ungranted user denied before resolve).
  - F9: ADR 0019 migration note (the `*`-grant → `manage:` behaviour change).
  286 .NET green. Operator cutovers + step-up UX still the only remaining work.
- 2026-06-15: **Phase C1 complete — caller authentication plane (ADR 0021).** Built
  the missing `/v1/broker` door: `CallerBrokerService` + `CallerBrokerEndpoint`
  (app-only caller token → verified `OidcJwt` caller; optional `X-Tessera-On-Behalf-Of`
  end-user; Mode P/U), two fail-closed gates (authenticator + `egress.enabled`),
  `/status` reflects it. Also fixed a real gap: `ProviderEgress` now
  authorization-audits **every** brokered call (the MCP path was unaudited too).
  ADR 0021 + the [cutover spec](caller-plane-and-mcp-cutover.md) (Q3 adversarial
  verification, gap analysis, homelab/UoEO use-case scoping) +
  [connect-a-domain-mcp.md](../connect-a-domain-mcp.md) onboarding. **302 .NET green.**
  Remaining: C2 (domain-MCP egress client, cross-repo) + C3 (operator cutover, real
  keys/egress) + C4 (mTLS phase 2, future).
- 2026-06-15 (cont.): **Standards-validated hardening + cutover enablers.** After an
  adversarial validation against the governing specs (MCP auth spec, RFC 8693, OWASP
  LLM06/NHI/SSRF, NIST 800-207) — which confirmed the architecture is the de-facto
  shape (no token passthrough + complete mediation) — shipped the gaps the cutover
  needs:
  - **SSRF hardening** (the one real finding): `AddressGuard` blocks the *resolved*
    IP (link-local/metadata `169.254.169.254`/loopback/multicast); `HttpClientTransport`
    resolves once + **pins** the IP at connect (closes DNS-rebind/TOCTOU). `SsrfGuard`
    gains `allowPlainHttp` (internal-host opt-in). +24 tests.
  - **Query-param egress**: `RecipeTool.Query` allow-list — only declared params are
    forwarded (URL-encoded); an agent can't smuggle one. +4 tests.
  - **`op=invoke`**: address a tool by its HTTP `(method, path)` so a domain MCP needs
    no second name map (`IProviderGateway.ResolveToolByHttp`). +5 tests.
  - **API-key-header injection** (`InjectionKind.ApiKeyHeader` + `injectionHeader`):
    the Servarr/Seerr/*arr class (`X-Api-Key`), which `bearer` couldn't express. +3.
  - **Audit coverage** (relentless audit): endpoint-level `/v1/broker` HTTP tests
    (auth, op routing, status→HTTP map) + the apikey DTO round-trip. **347 .NET green.**
  - **C2 done (cross-repo)**: `dragoshont/homelab_mcp` gained a credential-free
    `TesseraHttpClient` (same `.get/.post` surface, routes via `op=invoke`) + a
    per-service direct-vs-broker factory switch (default off). +17 py tests.
  - **Operator runbook**: `dragoshont/homelab` `docs/runbooks/tessera-media-broker-cutover.md`
    (the C3 activation: KV bundle-shaping, Authentik caller app, recipes/grants/
    bindings, NetworkPolicy, the two flips). C3 stays the operator step.
  - **Wiki**: a full Diátaxis wiki (`docs/wiki/`, 28 pages) — tutorial (verified
    runnable), how-to, reference (code-accurate), explanation. Non-native-English style.
  Remaining: C3 (operator cutover, real keys/live egress) + C4 (mTLS phase 2, future).
