# Current Gate Evidence

Worktree-bound continuation checkpoint, 2026-08-12. Baseline `611af03`; branch `2.0-beta`.

| Gate | Command | Result |
|---|---|---|
| Backend | `dotnet test Tessera.slnx --no-restore` | 786/786 PASS; fresh retained TRX |
| Shared client | `npm --prefix packages/tessera-client test` | 19/19 PASS |
| Shared typecheck | `npm --prefix packages/tessera-client run typecheck` | PASS |
| Web | `npm --prefix web test -- --pool=threads --maxWorkers=1` | 105/105 PASS |
| Web build | `npm --prefix web run build` | PASS; existing chunk-size warning |
| Web product/control E2E | focused desktop Playwright | 13/13 PASS |
| Web complete E2E | all configured desktop/phone Playwright projects | 44/44 PASS |
| Authenticated model E2E | live Web setup + Chat | one healthy account/profile/default set; `TESSERA LIVE OK` persisted/streamed |
| Reviewed install E2E | backend + desktop/phone + iOS typecheck | exact local package, review confirmation, disabled install; public results Inspect-only |
| iOS typecheck | `npm --prefix ios run typecheck` | PASS |
| Expo Doctor | `npx expo-doctor@latest` from `ios/` | 20/20 PASS after required `expo-font` peer |
| CocoaPods | `pod install` | CocoaPods 1.17.0; 107 pods |
| iOS Debug | `xcodebuild ... -configuration Debug ... build` | PASS |
| iOS Release | `xcodebuild ... -configuration Release ... build` | PASS; embedded `main.jsbundle` |
| iOS simulator | install/launch final Release on iPhone 17 Pro iOS 26.5 | PASS; renders verified live server/sign-in |
| macOS | lint, unit, Electron, package, packaged and installed smoke | 7/7 unit; all smoke/package gates PASS; 0 production vulnerabilities |
| K8s | `kubectl kustomize deploy/k8s` + `kubeconform -summary` | 7/7 valid |
| Custody-fix CI/image | GitHub Actions run `31619969671` | PASS; digest `04e1a046…297249` |
| Private GitOps | Flux revision `4fd4dbf` | PASS; rollout available |
| Cloudflare remote | forced public anycast request | PASS; `server: cloudflare`, `cf-ray`, strict descriptor |
| Persistence/restart | ephemeral backup + pod replacement | PASS; schema 15, DB/scheduler/plugin Ready |
| Auth boundary | remote setup/catalog and Electron CORS | 401 unauthenticated; `app://tessera` preflight 204 |
| Diff | `git diff --check` | PASS |
| Focused credential scan | workspace regex over `ios`, `packages`, final-delivery docs | no matches |
| Editor diagnostics | touched server/Web/iOS/shared source | no errors |

Owner Microsoft sign-in, automatic setup and real Web Chat pass. GitHub/Gmail/RM authorization, authenticated macOS/iOS continuation and physical-device journeys remain explicit user/provider/device checkpoints; no provider success is inferred.