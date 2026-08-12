# Final Diff

## Required

One canonical homelab server; real Web, packaged macOS and native iOS clients; secure local/remote access; server-owned state/Jobs/Actions; MCP/plugin-first Gmail/RM; stable route identity; complete diff and E2E evidence.

## Exists In Repository

Server identity descriptor, stable Tessera Home UUID, shared TypeScript domain/HTTP/route client, Web adoption, complete native iOS product surfaces, OIDC PKCE/Keychain/app lock, safe failover, diagnostics, notifications/deep links, tests and architecture/diff artifacts.

## Deployed / Installed

The prior schema-v15 Web/server release and macOS Alpha remain healthy/installed at digest `58223131…c10e91`. RM v0.5.38 and LiteLLM remain healthy. The new descriptor/shared-client release is not deployed. The standalone iOS Release build is installed on the iPhone 17 Pro simulator.

## Verified

- Core config: 23/23.
- Broker descriptor: 6/6.
- Shared client: 19/19.
- Web R2 client: 4/4; Web production build PASS.
- iOS TypeScript PASS; Expo Doctor 20/20; CocoaPods PASS; Debug and standalone Release simulator builds PASS; Release render and cold restart PASS.
- Baseline backend/Web/Desktop/deployment/RM evidence remains unchanged.

## External Blocks

Human OIDC sign-in; Gmail console/consent/safe write target; user and wife independent RM consent/MFA; physical iPhone signing/biometric/cellular dogfood.

## Engineering-Controlled Open Work

1. Publish/reconcile the new server image and verify descriptor.
2. Publish `tessera.hont.ro` through the existing Cloudflare Tunnel and verify streaming/OIDC.
3. Repackage macOS against the released Web bundle.
4. Execute authenticated Web/macOS/iOS and concurrency/network E2E.

Engineering-controlled `MISSING` and `PARTIAL` are therefore not zero. This report does not mislabel the repository checkpoint as final deployed delivery.
