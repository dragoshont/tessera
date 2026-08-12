# iOS E2E

## Executed

| Check | Result | Evidence |
|---|---|---|
| TypeScript compile | PASS | `npm run typecheck` |
| Expo compatibility | PASS | Expo Doctor 20/20 after required `expo-font` peer |
| Config identity | PASS | Tessera name, bundle, scheme, plugins, UUID resolved |
| CocoaPods | PASS | 1.17.0; 107 pods installed |
| Debug simulator build | PASS | `xcodebuild`, arm64+x86_64, `BUILD SUCCEEDED` |
| Install / app icon | PASS | iPhone 17 Pro iOS 26.5; Tessera icon visible |
| Scheme dispatch | PASS | first-use iOS confirmation displayed and accepted |
| Standalone Release build/render | PASS | embedded `main.jsbundle`; installed and rendered without Metro |
| Live route identity | PASS | final Release rendered `SERVER VERIFIED` and `Tessera Home` |
| Real sign-in action | PASS | system-browser action present; no raw server error/blank screen |
| Malicious identity / auth-before-probe | PASS | shared route tests, including empty UUID and token-order checks |
| Duplicate mutation / stale-route failover | PASS | keyed replay, unkeyed refusal, invalidation and in-flight probe tests |
| Deep-link allowlist / session fence | PASS | shared navigation and generation regressions |

## Requires Human OIDC

Microsoft sign-in completion, real Chat stream, Memory/Why, Gmail/RM shared Accounts, Jobs, Action approval and signed-device notification/cellular switching.

The Cloudflare descriptor/health route is live and verified separately through public anycast. No authenticated or provider-content evidence is fabricated. Physical-device biometric/cellular tests remain after signing/consent; App Store publication is not required.
