# Caller plane & domain-MCP credential cutover

> The design of record for closing the **caller authentication plane**
> ([ADR 0021](../adr/0021-caller-authentication-plane.md)) and using it to migrate a
> domain MCP's hidden upstream credentials into Tessera
> ([ADR 0015](../adr/0015-mcp-egress-through-tessera.md)). This spec is the
> adversarial verification, the gap analysis, the use-case review, and the phased
> build plan — written *before* the build, so the reasoning is on the record.
>
> Build/test prefix (always): `export PATH=/usr/local/share/dotnet:$PATH && hash -r`.

---

## 0. The question this answers

*"Does Tessera actually broker a domain MCP's hidden credentials end-to-end today,
and if not, exactly what is missing?"* — verified against the code, not the docs.

**Verdict:** No, not end-to-end — but the gap is **narrow and specific**, not
wholesale. Almost the entire spine is built and tested; one foundational door is
deliberately fail-closed, and the cutover artifacts (a credential-free egress client,
real recipes, the credential move) were never produced because that door was shut.

---

## 1. Adversarial verification (what is built vs. what is missing)

### Built and tested (286 .NET tests, verified by reading the code)

| Capability | Where | State |
|---|---|---|
| PDP: caller-must-be-verified, default-deny, planes (read/use/manage), step-up | `Core/Policy/PolicyDecisionPoint.cs` | **Built** |
| Caller (WHO) + end-user (FOR WHOM) identity split | `Core/Identity/{CallerIdentity,EndUserAssertion}.cs` | **Built** |
| App-only token → **verified** `CallerIdentity(OidcJwt)` | `Identity/TesseraTokenResult.cs` `ToCallerIdentity()` | **Built** |
| Provider egress: SSRF guard + result-class enforcement + templating | `Core/Egress`, `Broker/Egress`, `Providers` | **Built** |
| Recipes / grants / bindings + DTO round-trip | `Core/Recipes`, `Core/Policy` | **Built** (examples only) |
| Credential resolve + service-key fallback (ADR 0020) | `Core/Resolution/CredentialResolver.cs` | **Built** |
| Session refresh (rotation owner) | `Providers/SessionRefresher*`, `Broker/SessionRefreshService.cs` | **Built**, off by default |
| `IProviderGateway` dispatch the MCP surface already uses | `Mcp/IProviderGateway.cs`, `Broker/BrokerProviderGateway.cs` | **Built** |
| Native MCP surface (`tessera_*`) for the **chat** consumer | `Mcp/TesseraMcpService.cs`, `/mcp` | **Built** |

### The gap (verified, with the exact evidence)

1. **No caller authentication plane.** `POST /v1/broker` returns **503 by design**
   (`Broker/BrokerHost.cs`, `MapEndpoints`): *"no caller authenticator configured."*
   A non-chat workload has no way to present a verified caller identity.
2. **`/mcp`'s caller is hardcoded to one identity.** `TesseraMcpService` resolves a
   user token's caller to `_options.ChatCallerId` (`VerificationMethod.Network`) —
   correct for chat, useless for a distinct domain MCP. So per-caller grant scoping
   does not exist for automation.
3. **No credential-free egress client** in any domain MCP — the cutover artifact
   ADR 0015 §1 calls for (the domain MCP keeps its tools but gains a Tessera egress
   client and holds no secret) was never built, because there was nothing to
   authenticate to (gap 1).
4. **No *real* recipes/grants/bindings** for actual providers — only genericized
   examples (`grants.media.example.json` etc.). The operator config that names the
   real targets and binds them to credentials does not exist in-repo (correctly — it
   carries deployment specifics and lives in the private operator overlay).
5. **No onboarding for a non-chat caller.** `docs/getting-started.md` routes the
   automation caller to a README section that describes the fail-closed path — i.e.
   there is no working "connect a domain MCP" runbook.

### Correction to the earlier framing

The earlier characterization ("inbound MCP tool authorization is missing") was
**imprecise**. Inbound access — *who may open or call the MCP* — is the **access
gateway's** job (Authentik / oauth2-proxy), explicitly **not** Tessera's
([ADR 0018](../adr/0018-access-gateway-and-action-broker.md)). The domain MCP keeps
its own tool surface and its own gate. What Tessera owns is **credential custody +
per-action authorization of the upstream call**, and the precise missing pieces are
the five above — led by the **caller plane**.

---

## 2. The design (what we build)

Three repos, one seam.

```
   ┌─────────────┐  caller token (app-only, aud=Tessera)        ┌──────────────┐
   │ domain MCP  │  + optional on-behalf-of end-user token      │   Tessera    │
   │ (keeps its  │ ───────────────────────────────────────────▶│  /v1/broker  │
   │  tools, no  │   POST /v1/broker {target,tool,args,confirm} │              │
   │  secret)    │ ◀─────────────────────────────────────────── │  PDP→resolve │
   └─────────────┘   result (body + outputClass)                │  →egress     │
                                                                 └──────┬───────┘
                                                                        │ inject cred
                                                                        ▼
                                                                  upstream provider
```

### 2.1 Tessera repo (autonomous, this build) — the caller plane

- **`POST /v1/broker`** becomes the authenticated automation door (ADR 0021 phase 1):
  - Read the **caller token** (`Authorization: Bearer …`), validate with the existing
    `ITokenValidator`, require `IsAppOnly`, map to `CallerIdentity(OidcJwt)`.
  - Read the **optional** end-user token (`X-Tessera-On-Behalf-Of`) → verified
    `EndUserAssertion` (Mode U). With no end-user token the caller acts under its own
    service grant (Mode P). A grant-bound `actAs` (a service caller asserting a named
    principal *without* a token) is **deferred** — the PDP requires a present end-user
    to be independently verified, so asserting an unverified principal is denied by
    construction; building it needs a new verified-by-grant mechanism nothing yet uses.
  - Every `call` goes through `BrokerCore.HandleAsync` **first** (the audit + resolve
    spine — so each brokered call is authorization-audited exactly like `check`), then,
    on allow (or step-up + `confirm`), through the **existing** `IProviderGateway` for
    the egress. `list-tools` / `check` are read-only. No change to PDP/egress/recipes.
  - **Two fail-closed gates** (ADR 0021): the endpoint stays 503 unless a caller
    authenticator is configured (`identity.mode=oidc` + audience), and reaches no
    upstream until `egress.enabled`.
- A small, single-purpose `BrokerEndpoint` handler (testable without HTTP), mirroring
  `TesseraMcpService` but with a *verified* caller instead of the hardcoded chat id.
- **Onboarding doc**: "connect a domain MCP as a caller" — register an app identity,
  obtain an app-only token (aud=Tessera), grant `caller: <mcp> may <plane>:* on
  <target>`, flip `egress.enabled`.

### 2.2 Domain MCP repo (cross-repo, next build) — credential-free egress client

- A thin Tessera egress client: for each HTTP-injectable tool, instead of calling the
  upstream directly with an injected key, `POST /v1/broker` with `{target, tool, args,
  confirm}` and the caller token (Mode P) or the forwarded end-user token (Mode U).
- The MCP **keeps its tool names, shapes, and ergonomics** (ADR 0015 shape C); it
  **drops the upstream secrets** from its environment.
- SSH-backed tools are **untouched** (see §3).

### 2.3 Operator overlay (cutover, operator STOP) — real recipes + credential move

- Real recipes (`target`, `baseUrl`, tools as `(name, method, path, plane,
  outputClass)`), grants (`caller: <mcp>` scoped per plane), bindings (`owner:
  service`, the credential reference).
- Move the upstream keys from the MCP's secret into Tessera's store (KV), referenced
  by binding — the MCP no longer reads them.
- Flip `egress.enabled`, add the SSRF allow-list, set the NetworkPolicy so only the
  MCP can reach `/v1/broker`. **Security-sensitive: real secrets + live egress →
  operator.**

---

## 3. Use-case review (which tools actually fit)

A domain MCP is **not** uniform. Classifying a real 133-tool homelab MCP against the
egress model (ADR 0014) is the honest scoping step.

### 3.1 HTTP-injectable → **fits** (the migration target, ~60 tools)

API-key / bearer / cookie-session over HTTP. These are exactly what ADR 0014 brokers:

- **API-key header** (`X-Api-Key`): the *arr family (Sonarr/Radarr/Prowlarr/Lidarr/
  Readarr), the request portal (Overseerr-style). Cleanest fit.
- **Bearer / token param**: media server (token param), DNS provider (bearer). Clean.
- **Cookie session** (login → cookie + CSRF): torrent client, network controller.
  Fits via the cookie-portal egress + session-owner refresh (ADR 0014 Phase B), with
  Tessera as the **sole** session owner.
- **Bearer + device IP** (home-automation hub): bearer fits; the device IP is part of
  the recipe `baseUrl`.

### 3.2 Device-paired → **does not fit** (small tail)

Asymmetric HomeKit-style pairing (PIN + pinned cert) is not an HTTP-injectable
credential; cert pinning breaks the egress proxy model. Leave native.

### 3.3 SSH-backed platform tools → **out of scope** (different credential class, ~56 tools)

The platform family (`kube_*`, `host_*`, `flux_*`, `backup_*`, `ansible_*`, `cert_*`,
`dns_*`, `netdata_*`) executes **shell over SSH** with a private key. This is **not**
HTTP-injectable, and *"let agents run arbitrary shell"* is an **explicit Tessera
non-goal** (service-access spec). These keep their own credential, gated by the access
gateway + NetworkPolicy + the MCP's own read-only/audit controls. A future "command
broker" egress driver could revisit this — **explicitly not now.**

> **Honest finding worth stating plainly:** the *ops/log-hunting* use case (read
> Grafana/journal logs, find a failing pod, check Flux) maps to **exactly** the
> SSH-backed tools in §3.3 — the ones that **do not** go through this plane. Brokering
> a domain MCP's credentials is the right investment and closes a real custody gap for
> the **HTTP-injectable** providers (§3.1); it is **orthogonal** to the SSH-backed ops
> tooling. Don't oversell the cutover as covering the ops co-pilot path — it doesn't,
> by design.

### 3.4 A static, credential-free MCP → **no Tessera** (zero benefit)

A read-only MCP that serves a corpus baked into its image (no upstream call, no
secret) has **nothing to broker**. Integrating it with Tessera would add a dependency
for zero security benefit. Deliberately **not** integrated. (The reviewed UoEO course
MCP is exactly this case.)

---

## 4. Build plan (phased, no scaffolding)

Convention (per the action-broker plan): implement for real → adversarial self-review
→ `dotnet test Tessera.slnx` green → PII gate → commit → push. A **🛑 STOP — operator**
task is a real-secret / live-egress step the agent builds up to and stops.

### Phase C1 — caller plane (Tessera repo · autonomous) ✅ this build

- [ ] **C1.1** `BrokerEndpoint` handler in `Tessera.Broker`: caller-token auth
  (app-only required) → `CallerIdentity`; optional `X-Tessera-On-Behalf-Of` → verified
  end-user; body `{target, tool, args, confirm, actAs?}`; dispatch via
  `IProviderGateway` / `BrokerCore`. Pure handler, HTTP-free, unit-tested.
- [ ] **C1.2** Wire `POST /v1/broker` to the handler **only** when a caller
  authenticator is configured; otherwise keep the 503. Add `list-tools` + `check`
  sub-actions (read-only) alongside `call`. Reflect the live state in `/status`
  (`BrokerEndpoint: enabled|fail-closed`).
- [ ] **C1.3** Tests: app-only caller allowed; user-token-as-caller rejected;
  unverified/none → 503/deny; Mode U end-user forwarded + PDP-gated; Mode P (no
  end-user) under a caller grant; egress-disabled → `notallowed`; write needs
  `confirm`; every `call` is audited (decision recorded via `BrokerCore`).
- [ ] **C1.4** Onboarding doc `docs/connect-a-domain-mcp.md` + a getting-started link;
  update the implementation plan + ADR statuses.
- [ ] **C1.5** Adversarial self-review + PII gate + commit + push.

### Phase C2 — credential-free egress client (domain MCP repo · cross-repo)

- [ ] **C2.1** A `TesseraEgress` client in the domain MCP; route the §3.1 tools
  through `/v1/broker`; keep tool ergonomics; drop the upstream secrets.
- [ ] **C2.2** Caller-token acquisition (app-only, aud=Tessera) + the on-behalf-of
  header for Mode U. SSH/device tools untouched.

### Phase C3 — operator cutover (private overlay) · 🛑 STOP — operator

- [ ] **C3.1 🛑** Real recipes/grants/bindings (`owner: service`) for the §3.1
  providers; move the keys into Tessera's store; `egress.enabled` + SSRF allow-list +
  NetworkPolicy. Real secrets + live egress → operator.

---

## 5. Security review of this design

- **Fail-closed, twice.** The caller plane needs an authenticator *and* `egress.enabled`
  — two independent switches, both default-off. Deploying opens nothing.
- **No new secret to custody.** Phase 1 reuses signed app-only tokens (no shared
  static secret); the rejected pre-shared-token option is dev-loopback only.
- **Caller ≠ subject.** A user token presented on the *caller* header is rejected; a
  human is a subject (FOR WHOM), never a workload (WHO). Mode U requires the end-user
  to be independently verified by the PDP.
- **act-as is grant-bound.** Mode P `actAs` is default-deny; only a grant authorizes a
  caller to assert a principal (ADR 0015 §4).
- **SSRF unchanged.** Egress still flows through the existing SSRF-guarded transport;
  the caller plane adds an authenticated front door, not a new egress path.
- **Scope honesty.** SSH-backed tools are excluded by design (non-goal), not forgotten;
  the static credential-free MCP is excluded for zero-benefit.
