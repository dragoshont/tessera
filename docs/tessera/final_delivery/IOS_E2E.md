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
| Fail-closed product UI | PASS | old deployed server returned invalid descriptor; app showed Offline and sent no auth |
| Cold app restart | PASS | settled screen restored the same fail-closed state |
| Malicious identity / auth-before-probe | PASS | shared route tests, including empty UUID and token-order checks |
| Duplicate mutation / stale-route failover | PASS | keyed replay, unkeyed refusal, invalidation and in-flight probe tests |
| Deep-link allowlist / session fence | PASS | shared navigation and generation regressions |

## Requires Deployed Descriptor And Human OIDC

Verified descriptor, OIDC system-browser sign-in, real Chat stream, Memory/Why, Gmail/RM shared Accounts, Jobs, Action approval, notification click-through, Wi-Fi route, cellular/Cloudflare route and Wi-Fi→cellular→Wi-Fi.

No authenticated or provider-content evidence is fabricated. Physical-device biometric and cellular tests remain after signing/consent; App Store publication is not required.
