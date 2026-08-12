# iOS Failure Analysis

## Symptom

The native app built but appeared not to load in real use. A standalone Release launch rendered the bounded offline screen instead of the product.

## Root Cause

The deployed server is stale. `GET /.well-known/tessera` returns the Web SPA document rather than the required six-field descriptor. The client correctly rejects that route before OIDC/authentication, so it cannot enter the product. The failure is route identity, not Metro, React Native registration or a client-side canonical store.

## Fix

- Added the fail-closed server descriptor and stable operator-owned installation identity.
- Added a shared route/auth client with TLS-only origins, bounded reads, timeouts, generations, invalidation and no auth before descriptor verification.
- Added actionable offline/retry/connection details UI instead of an infinite loader.
- Moved the dogfood hostname and installation UUID into ignored `.env.local`; public source defaults are generic.
- Regenerated the native project and embedded the Release JavaScript bundle.
- Added setup readiness, Accounts runtime state and server-side plugin search to native screens.

## Verification

- TypeScript: PASS.
- Expo Doctor: 20/20 at the prior checkpoint.
- CocoaPods: PASS.
- Standalone Release simulator build after current setup/search/config changes: PASS.
- Release render and cold restart against the stale server: bounded offline UI, no crash/blank screen.

## Remaining Runtime Check

After cutover: install/launch the final Release, verify descriptor/auth and all primary screens against the deployed server. Physical iPhone Wi-Fi/cellular testing remains dependent on available signing/device access and is not inferred from simulator success.