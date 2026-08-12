# Current State Snapshot

Captured 2026-08-12 before the reality-audit implementation.

## Source

- Repository: `/Users/dragoshont/Repo/tessera`
- Branch: `2.0-beta`
- HEAD: `611af03` (`Record final RM v0.5.38 rollout evidence`)
- Worktree: dirty continuation checkpoint; no reset/clean performed.
- Modified tracked files: server identity/version/config, Web shared-client adoption, deployment config, server/Web/Desktop/iOS delivery reports and focused tests.
- Untracked source: native `ios/`, shared `packages/tessera-client/`, ADR 0034 and final-delivery diff/evidence artifacts.

## Running Reality At Capture

- Stable URL: `https://tessera.hont.ro`
- Local split DNS: private Traefik LAN address (value kept in private operations config).
- `GET /.well-known/tessera`: HTTP 200 `text/html`, 457-byte SPA index, proving the descriptor release is not deployed.
- Last recorded Tessera image: `sha256:582231318e739de0ab6141027209a4140b17c55e1123c79bd87ef117b4c10e91`, source `f869d76`.
- Last recorded GitOps revision: `1a584f8`.
- Database: recorded SQLite schema v15 on retained data and backup PVCs; live revalidation required.
- Remote-access correction: Cloudflare Tunnel is controlling. Prior Tailscale `/32` requirements are stale assumptions.

## Clients

- Web: deployed SPA at the stable URL, but currently backed by the stale descriptor-less server release.
- macOS: Alpha 0.1.0 artifacts and `/Applications/Tessera.app` recorded; reality audit/repack required.
- iOS: Expo SDK 57 / React Native app exists in the dirty worktree. Debug and standalone Release simulator builds passed; Release rendered a fail-closed offline screen because the deployed server lacks the descriptor. User reports the app is not loading in real use; this remains a blocker until deployed/authenticated runtime E2E passes.

## Tests At Capture

- Full backend: 773/773 PASS.
- Shared TypeScript client: 19/19 PASS.
- Web: 105/105 PASS and production build PASS.
- iOS: typecheck, Expo Doctor 20/20, CocoaPods, Debug/Release simulator builds, render and restart PASS.
- Public Tessera K8s render: 7/7 valid.
- Independent changed-code adversarial verdict: PASS.

## Provider / Plugin Snapshot

- Regina Maria MCP: recorded v0.5.38, SDK 1.28.1, two isolated server connectors; actual account/session state must be queried without reading secrets.
- LiteLLM: recorded real completion; actual deployed gateway/model/default-profile state must be queried.
- GitHub: user reports already configured; canonical ConnectedAccount/health bridge must be audited.
- Google OAuth/Gmail: user reports configured; distinguish OAuth app readiness from user ConnectedAccount authorization.
- Secondary Regina Maria account: must remain independently authorized and must not be inferred connected from plugin health.

## Audit Work Queue

1. Reconcile actual homelab/Cloudflare/GitOps/runtime state.
2. Replace stale Tailscale controlling assumptions.
3. Fix iOS loading against the corrected deployed server.
4. Discover/import existing model, account and plugin configuration idempotently.
5. Implement server-side extensible integration catalog/search and safe inspect/install flow.
6. Audit every visible Web/macOS/iOS control; implement, explain-disable or remove.
7. Build, publish, deploy, repackage/install and run real cross-client E2E.
