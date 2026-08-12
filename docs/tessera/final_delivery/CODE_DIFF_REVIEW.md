# Code Diff Review

Comparison baseline: `611af03`.

| Class | Change | Review |
|---|---|---|
| server/core | stable `serverIdentity`, strict validation, public cache-disabled descriptor, setup/bootstrap and provider-neutral catalog projection | additive; no schema migration |
| plugins/MCP | provider-owned setup descriptors and public repository catalog adapter | provider endpoints/parsing stay outside Core/Broker; public metadata centrally downgraded |
| Web | shared client adoption, automatic Setup, truthful integration readiness and searchable Plugins | 105 unit and 42 desktop/phone E2E pass; Setup/Search stories build |
| macOS | no Electron-native change; packaged renderer consumes the rebuilt shared Web bundle | repackaged, installed and renderer-ready smoke passes |
| iOS | new Expo SDK 57 native client: five tabs, OIDC PKCE, Keychain refresh/fence, app lock, resumable SSE, Jobs, Accounts/connect, Plugins, Memory/Why, Activity, exact Action review, diagnostics, allowlisted deep links | TS clean; Expo Doctor 20/20; CocoaPods; Debug+Release build; standalone render/restart |
| deployment | public K8s identity remains fail-closed; standalone example uses an allowlisted non-zero placeholder; private GitOps owns the real UUID | public 7/7 schema-valid; Flux rollout and Cloudflare route applied under the user's controlling authorization |
| security | TLS+UUID route trust, no auth before verification, bounded responses, safe failover, keyed/serialized setup, central catalog downgrade | shared 19/19 plus concurrent bootstrap/malicious catalog regressions |
| tests | full backend/plugin/architecture, shared routing, Web, native Release, Electron and deployment suites | backend 786, shared 19, Web 105 + E2E 44; all current gates pass |
| docs | ADR 0034, contracts, snapshot and final diff set | no secret/PII evidence |

The change adds no database migration, provider-specific Broker capability, client scheduler, client canonical database, arbitrary endpoint editor, certificate bypass, WebView, or offline mutation queue.
