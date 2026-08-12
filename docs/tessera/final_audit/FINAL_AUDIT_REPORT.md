# Final Audit Report

Status: `DELIVERED_ENGINEERING_AUTH_CHECKPOINTS_REMAIN`.

## Corrections

The previous report treated Tailscale `/32`, deployment approval, macOS repack and an iOS compile as controlling blockers. Runtime truth showed the homelab already uses Flux-managed MicroK8s, Traefik, External Secrets/Azure Key Vault, GHCR and a two-replica Cloudflare Tunnel. The iOS app compiled but correctly failed route verification because the deployed server returned SPA HTML instead of the Tessera descriptor.

## Homelab And Cloudflare

Tessera is one stateful, single-writer server in the `tessera` namespace with retained data/backup PVCs. Cloudflare proxied DNS and the existing tunnel route the canonical hostname to `tessera.tessera.svc.cluster.local:8080`; a forced public-anycast request returned `server: cloudflare`, `cf-ray`, the strict six-field descriptor and healthy origin. Tailscale is not required.

The user supplied explicit Tessera-specific deployment authorization in the controlling mandate. The manager reviewed rendered manifests/policy, committed private GitOps revisions, pushed `main`, invoked Flux reconciliation and observed the exact immutable image. No unrelated workload, secret value or apply-shaped infrastructure command was used outside that approved GitOps flow.

## iOS

The final standalone Release embeds `main.jsbundle`, installs and launches without Metro, and renders `SERVER VERIFIED`, `Tessera Home` and the system-browser Sign in action. The blank/offline blocker is closed. The unsigned simulator logs a non-fatal Expo Notifications Keychain registration error; signed physical-device biometric, notification and cellular switching remain device checkpoints.

## Setup And Existing Configuration

Web now needs no server URL, LiteLLM URL/key, model profile or default-model entry. Authenticated setup validates the existing LiteLLM gateway/key, maps the logical account credential reference to a valid deterministic Key Vault name, creates one healthy model account/profile and both defaults, and opens Chat. Real Web Chat persisted and streamed `TESSERA LIVE OK`.

GitHub, Gmail and Regina Maria runtime readiness is shown separately from account authorization. GitHub/Gmail/RM correctly remain Ready to connect until their own consent/session validates. A secondary RM identity remains isolated and is never inferred Connected from plugin health.

## Controls And Plugin Discovery

The dead All connections route was removed. Account disable, Job cancel and other destructive actions require confirmation; past Job schedules and invalid timezones are blocked. The complete desktop/phone control matrix passes.

Plugins search the hash-pinned local catalog, official MCP Registry and provider-owned public repository metadata. Results expose source, publisher, runtime, version, license, trust, capabilities, auth and sensitivity. Public candidates are centrally downgraded and Inspect-only. Exact local `id@version` packages already present in the reviewed server image support explicit Web/iOS review and keyed installation in a disabled state. Clients cannot submit executable code, commands, endpoints, manifests, hashes or trust levels.

All five reviewed packages were already installed for the live owner, so no canonical plugin was removed to manufacture a live install demo. Backend and desktop/phone E2E verify disabled, idempotent, owner-scoped reviewed installation and public-result refusal.

## Deployment And Verification

- Final CI run `31625718882`, immutable image `835f28b2...44bc38` from source `4e60505`, and Flux/GitOps `ca2b1e8`: PASS.
- Descriptor/no-store, Cloudflare TLS route, DB schema 15, PVCs, scheduler, plugin registry, online backup integrity and replacement-pod recovery: PASS.
- Fresh encrypted off-node restic snapshot coverage: observed. Isolated off-node restore: not claimed; root-only operations checkpoint.
- Backend/plugin/architecture: 786/786 PASS with fresh retained TRX evidence.
- Shared route/auth client: 19/19 PASS.
- Web: 105/105 unit; 44/44 desktop/phone Playwright; production and Storybook builds PASS.
- macOS: current package/installed renderer smoke and production dependency audit PASS.
- iOS: typecheck, Expo Doctor, CocoaPods and standalone Release build/render PASS.
- Public K8s: 7/7 schema-valid; private Tessera render valid with CRDs explicitly skipped.
- PII/secret, diff and Architrave run-artifact gates: PASS.
- Two independent source judge families and final runtime/product review: PASS.

## Real E2E Executed

Executed against the deployed product: Cloudflare-anycast descriptor/health, OIDC redirect to Microsoft and completed owner login, automatic LiteLLM bootstrap, Key Vault custody metadata, real persisted/streamed Chat, unauthenticated API refusal, Electron CORS, iOS verified-server render, macOS packaged inspection, backup/schema/count continuity and pod restart recovery.

## External Checkpoints

- Authorize GitHub and run a safe read.
- Complete Gmail consent, then a safe read; send only to a human-approved target.
- Complete primary and secondary Regina Maria login/MFA independently, then safe reads.
- Continue the same canonical Conversation/Memory/Job/Action across authenticated macOS and iOS sessions.
- Run signed physical-iPhone biometric, notification click-through and Wi-Fi/cellular switching.

No password, CAPTCHA or MFA bypass is attempted. `DELIVERED_E2E` is not claimed until those account-holder/provider/device checkpoints pass.
