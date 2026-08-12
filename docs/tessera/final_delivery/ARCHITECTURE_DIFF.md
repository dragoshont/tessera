# Architecture Diff

## Before

Web and packaged Electron used one server, but no iOS client, installation identity handshake, route failover contract, or connection diagnostics existed.

## After

- One .NET server still owns Conversations, Messages, Memory, Jobs, Accounts, Plugins, Actions, Evidence and Activity.
- `@tessera/client` is transport-neutral shared TypeScript for strict descriptors, routes, problem responses, idempotency and common DTOs.
- Web injects its existing canonical origin/auth-header transport.
- iOS injects Keychain access tokens and Expo Crypto idempotency UUIDs into a verified `RouteManager`.
- Native route acceptance requires standard TLS plus exact installation UUID, API `v1`, protocol `1`, exact bounded descriptor fields and no redirect.
- Reads may fail over once. Mutations may fail over only with the same existing idempotency key, method, path, body and auth snapshot.
- iOS stores session and diagnostic metadata only. It has no canonical DB, scheduler, provider credential, integration client, Action engine or durable offline queue.
- Gmail and Regina Maria remain plugins/MCP. LiteLLM remains server-side.
- Actions remain the only consequential side-effect boundary; native approval submits only Action ID/version.

## Verdict

The server/client topology converges without provider leakage. Remote access reuses the homelab Cloudflare Tunnel; local split DNS may keep Traefik as the direct path for the same hostname.
