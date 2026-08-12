# iOS Architecture

## Stack

Expo SDK 57, React Native 0.86.2, TypeScript 6, Expo Router stable tabs, native SF Symbols. Bundle ID `ro.hont.tessera`; scheme `tessera`; minimum generated iOS target 16.4.

## Boundaries

- `RouteManager`: TLS origin validation, descriptor identity, diagnostics, safe failover.
- `SessionProvider`: OIDC PKCE, Keychain token rotation, principal, app lock, network transition reconnect.
- `TesseraApi`: typed canonical R2 REST/SSE calls only.
- screens: native presentation; no raw token/origin/provider access.

There is no WebView, local database, scheduler, provider implementation, provider credential, Action engine, canonical cache or offline mutation queue.

## Navigation

Five stable tabs: Chat, Jobs, Accounts, Memory, More. More contains the Action inbox plus Plugins, Activity and Settings. Action review is a native modal with exact capability/account/target/payload/expiry and explicit approve/cancel confirmations.

## Security

OIDC uses the system browser and Authorization Code + PKCE. Access and refresh tokens use `WHEN_UNLOCKED_THIS_DEVICE_ONLY` Keychain storage. Refresh is serialized and occurs only after route verification. Cold/background restoration engages Face ID/Touch ID/device fallback. ATS arbitrary loads are false. Expo Crypto produces mutation idempotency UUIDs.

## Native Platform Features

- local notifications and Router deep links;
- network state listener and verified reconnect;
- app lock;
- Keychain;
- dark/light adaptive palette;
- SF Symbol tab/command icons;
- generated Tessera icon/splash assets.

External APNs is not required by the R2 product spec. Jobs continue on the server while iOS is closed.
