# Code Diff Review

Comparison baseline: `611af03`.

| Class | Change | Review |
|---|---|---|
| server/core | stable `serverIdentity`, strict validation, public cache-disabled descriptor, assembly-derived version | additive; no schema/data/provider change |
| plugins/MCP | no provider implementation changes | boundary preserved |
| Web | R2 types/problem/idempotency transport moved to `@tessera/client`; existing auth/origin injected | focused 4/4 tests and production build pass |
| macOS | no Electron-native change; packaged renderer consumes the rebuilt shared Web bundle | requires repack only after deployed release |
| iOS | new Expo SDK 57 native client: five tabs, OIDC PKCE, Keychain refresh/fence, app lock, resumable SSE, Jobs, Accounts/connect, Plugins, Memory/Why, Activity, exact Action review, diagnostics, allowlisted deep links | TS clean; Expo Doctor 20/20; CocoaPods; Debug+Release build; standalone render/restart |
| deployment | stable Tessera Home UUID added to K8s config; example retains an invalid all-zero sentinel | render/validate before release; not applied |
| security | TLS+UUID route trust, no auth before verification, bounded descriptor, safe failover, no unkeyed mutation replay | 8 shared route tests pass |
| tests | Core/Broker descriptor tests, empty UUID, route/browser transport, timeout/race/fence/navigation suites | Core 23/23, Broker 6/6, shared client 19/19 |
| docs | ADR 0034, contracts, snapshot and final diff set | no secret/PII evidence |

The change adds no database migration, provider-specific Broker capability, client scheduler, client canonical database, arbitrary endpoint editor, certificate bypass, WebView, or offline mutation queue.
