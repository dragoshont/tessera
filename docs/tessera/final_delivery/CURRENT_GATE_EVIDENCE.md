# Current Gate Evidence

Worktree-bound continuation checkpoint, 2026-08-12. Baseline `611af03`; branch `2.0-beta`.

| Gate | Command | Result |
|---|---|---|
| Backend | `dotnet test Tessera.slnx --no-restore` | 780/780 PASS |
| Shared client | `npm --prefix packages/tessera-client test` | 19/19 PASS |
| Shared typecheck | `npm --prefix packages/tessera-client run typecheck` | PASS |
| Web | `npm --prefix web test -- --pool=threads --maxWorkers=1` | 105/105 PASS |
| Web build | `npm --prefix web run build` | PASS; existing chunk-size warning |
| Web product/control E2E | focused desktop Playwright | 13/13 PASS |
| Web complete E2E | all configured desktop/phone Playwright projects | 42/42 PASS |
| iOS typecheck | `npm --prefix ios run typecheck` | PASS |
| Expo Doctor | `npx expo-doctor@latest` from `ios/` | 20/20 PASS after required `expo-font` peer |
| CocoaPods | `pod install` | CocoaPods 1.17.0; 107 pods |
| iOS Debug | `xcodebuild ... -configuration Debug ... build` | PASS |
| iOS Release | `xcodebuild ... -configuration Release ... build` | PASS; embedded `main.jsbundle` |
| iOS simulator | install/launch on iPhone 17 Pro iOS 26.5 | PASS; render and cold restart; fails closed against old server |
| macOS | lint, unit, Electron, package, packaged and installed smoke | 7/7 unit; all smoke/package gates PASS; 0 production vulnerabilities |
| K8s | `kubectl kustomize deploy/k8s` + `kubeconform -summary` | 7/7 valid |
| Diff | `git diff --check` | PASS |
| Focused credential scan | workspace regex over `ios`, `packages`, final-delivery docs | no matches |
| Editor diagnostics | touched server/Web/iOS/shared source | no errors |

The server descriptor, Cloudflare Tunnel publication, authenticated/provider journeys and macOS repack remain explicitly undeployed/unverified. No command above is evidence of those later gates.