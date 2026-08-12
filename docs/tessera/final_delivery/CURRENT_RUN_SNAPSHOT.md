# Current Run Snapshot

Captured 2026-08-12 before the continuation mandate implementation.

## Source State

- Repository: `/Users/dragoshont/Repo/tessera`
- Branch: `2.0-beta`
- Baseline HEAD: `611af03` (`Record final RM v0.5.38 rollout evidence`)
- Remote parity at capture: `origin/main` and `origin/2.0-beta` include the baseline.
- Worktree: clean; no staged, modified or untracked files.
- Current subagent work: no active implementation subagent. Prior read-only Claude/Architrave adversaries completed; no terminal-owned mutation is in progress.

Recent delivery commits, newest first:

```text
611af03 Record final RM v0.5.38 rollout evidence
f869d76 Require Regina Maria MCP v0.5.38
b04e3d0 Require Regina Maria MCP v0.5.37
7b05565 Record deployed Web and Desktop evidence
f9ff112 Make packaged Desktop smoke deterministic
8cb3641 Isolate Electron smoke test state
242bf59 Fail closed identity and bound product storage
feb37d2 Fix stable OIDC subject ownership
26bfc71 Deliver Tessera Web and Desktop alpha
```

## Implemented Product

- Canonical .NET 10 server with SQLite schema v15.
- React Web product routes: Chat, Jobs, Accounts, Plugins, Memory, Activity and Settings.
- Electron macOS client reusing the React product UI and canonical server.
- Server-owned scheduler, Accounts, Plugins, MCP runtime, Actions, Evidence and Activity.
- Gmail and Regina Maria implementations remain behind plugins/MCP; no provider implementation is in Core/Broker.
- No iOS or other mobile project exists at this baseline.
- No stable server-identity handshake, multi-route native client selector or product-visible connection diagnostics exist at this baseline.

## Deployed Reality

- URL: `https://tessera.hont.ro`
- Homelab GitOps revision: `1a584f8`, Flux Ready.
- Tessera image: `ghcr.io/dragoshont/tessera@sha256:582231318e739de0ab6141027209a4140b17c55e1123c79bd87ef117b4c10e91`
- Regina Maria image, both isolated accounts: `ghcr.io/dragoshont/reginamaria-mcp@sha256:b51b7f13670bb1018b69fb335b176716b320122b82c200358bef78922035217a` (v0.5.38, MCP SDK 1.28.1).
- Tessera, RM account A and RM account B pods: `1/1 Running`, zero restarts at final observation.
- Persistent retained data and backup PVCs are Bound.
- Verified backup: integrity OK, schema v15.
- Full Tessera restart recovered the same database and immutable image.
- Default-deny Tessera namespace egress permits declared LiteLLM/RM/OIDC/Key Vault routes and denied an undeclared private Sonarr route.
- OIDC discovery and strict Web PKCE authorization are active for client `tessera-app`.

## Test and Package Evidence

- Backend: 769 tests PASS.
- Web: 105 Vitest tests PASS; 34 Playwright checks PASS.
- Desktop: 7 unit/security tests PASS; development Electron and hardened packaged-binary readiness PASS.
- Desktop dependency audit: zero vulnerabilities.
- Complete homelab render: 521 unique resources; strict kubeconform produced zero invalid resources/errors with SOPS Secrets validated separately.
- RM v0.5.38: 159 tests PASS; Ruff and startup import PASS.
- Real LiteLLM completion: HTTP 200 on `claude-haiku-4.5`.
- No-token RM mutation probes on account A and account B: rejected locally before provider access.

Desktop package/install state:

```text
desktop/release/Tessera-Alpha-0.1.0-arm64.dmg
desktop/release/Tessera-Alpha-0.1.0-arm64.zip
/Applications/Tessera.app
```

## Existing Reports

Canonical evidence is under `docs/tessera/final_delivery/`, including the current baseline, deployment, Web/Desktop, LiteLLM, RM, backup, security and final delivery reports. Architrave run artifacts exist under `.architrave/runs/`, with the latest delivery work rooted in the 20260811 runs.

## Known External Checkpoints

- User OIDC sign-in/consent in the installed Desktop app.
- Google provider-console callback registration if the existing client does not accept the Tessera callback.
- Gmail user consent and safe send target.
- User RM ConnectedAccount authorization in Tessera.
- Wife's independent RM authorization/MFA/consent.
- Apple public distribution signing/notarization credentials.
- Physical iPhone signing/dogfood when available.

These checkpoints do not block engineering-controlled iOS implementation, stable server identity, route selection, diagnostics, simulator validation, packaging, requirement diffs or deployed route verification.