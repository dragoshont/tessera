# Tessera for iOS

Native Expo SDK 57 client for the canonical Tessera home server. Product state, Jobs, Actions, Memory, Accounts and provider credentials remain server-owned.

## Configure

Set the non-secret installation identity before building:

```bash
export EXPO_PUBLIC_TESSERA_SERVER_ID='<stable installation UUID>'
export EXPO_PUBLIC_TESSERA_REMOTE_ORIGIN='https://tessera.example'
# Optional distinct TLS-verified local route:
export EXPO_PUBLIC_TESSERA_LOCAL_ORIGIN='https://tessera.lan.example'
```

The repository default binds the Tessera Home dogfood installation. Override it for any other installation. There is no trust-on-first-use or arbitrary endpoint editor.

## Build

```bash
npm install
npm run typecheck
npm run prebuild -- --platform ios
npm run ios
```

OIDC uses Authorization Code with PKCE in the system browser. Session material is stored in iOS Keychain. Face ID/Touch ID lock requires a development build or release build, not Expo Go.

## Dependency audit

SDK 57 currently carries transitive npm advisories through Expo CLI/Metro (`image-size`) and Xcode project tooling (`uuid`). They process developer-controlled build inputs and are not imported by Tessera product code. Do not run `npm audit fix --force`: its proposed major-version changes are incompatible with Expo SDK 57. Reassess when Expo publishes a compatible patched SDK.
