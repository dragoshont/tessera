# Security Diff

## Web

Existing OIDC Authorization Code + PKCE, browser origin, CSP and owner-scoped API behavior remain. Web now injects its auth/origin into the shared problem/idempotency client. Focused tests and production build pass.

## Electron

Existing `nodeIntegration=false`, `contextIsolation=true`, `sandbox=true`, `webSecurity=true`, narrow preload, deep-link allowlist and packaged secret checks remain. No new IPC/native privilege was introduced.

## iOS

- System AuthSession browser with PKCE; no WebView.
- Access/refresh tokens only in Keychain with `WHEN_UNLOCKED_THIS_DEVICE_ONLY`.
- Refresh occurs only under a current server-verification lease, rotates Keychain state and is fenced so sign-out cannot be undone by in-flight refresh/reconnect work.
- Cold/background app lock uses Face ID/Touch ID/device fallback.
- ATS arbitrary loads are false; route origins require HTTPS except loopback test origins.
- No Gmail/RM/LiteLLM secret or canonical state is stored.
- Deep links accept only exact allowlisted product paths; traversal, queries, fragments and external URLs are rejected.
- Exact Action scope/payload is visible before native confirmation.

## Server And Plugins

The public descriptor contains only non-secret identity/version metadata and is cache-disabled. Missing/invalid identity fails closed. Provider capabilities remain in plugins/MCP; policy, Actions, evidence and credentials remain server-owned.

## Route Adversaries

| Attack | Control | Evidence |
|---|---|---|
| malicious LAN Tessera | expected UUID plus TLS | mismatch test; no auth header on probe |
| descriptor memory abuse | content-length and streamed 4096-byte bound | oversized test |
| discovery redirect | `redirect: manual`, exact status | route test path |
| duplicate failover write | replay only keyed byte-identical request | replay/unkeyed tests |
| transient local gateway | verify alternate before read/keyed retry | 503 failover test |
| stale token / logout race | serialized OIDC refresh after route verification plus generation fence | generation regression + native gates |

## Residual Risk

Expo SDK 57 has 22 transitive npm advisories in Metro image parsing and Xcode/UUID build tooling. No vulnerable parser is called by Tessera runtime code; forced npm remediation breaks SDK compatibility. Upgrade on a compatible Expo release. Physical-device Keychain/biometric and authenticated route-transition adversaries remain live E2E work.
