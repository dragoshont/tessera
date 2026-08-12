# Requirements Diff

Baseline: `611af03`. Status reflects repository and last verified runtime evidence, not intent.

| ID | Requirement | Source mandate | Implementation location | Server | Web | macOS | iOS | Deployment | Test/E2E evidence | Status | Gap | Action |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| R01 | One canonical server/state | continuation §§3,18 | `src/`, SQLite schema v15 | yes | yes | yes | yes | deployed old release | persistence/restart baseline | PASS | none | retain |
| R02 | Stable server identity | §§8-10 | `BrokerHost`, `TesseraConfig`, ADR 0034 | implemented | canonical URL | canonical URL | strict UUID verify | config ready, image old | Core 23/23; Broker 6/6; client 8/8 | PARTIAL | descriptor not deployed | publish/reconcile after approval |
| R03 | Verified local/remote selection | §§8-12 | `packages/tessera-client` | descriptor | canonical URL | fixed canonical URL | canonical Cloudflare route with verified identity | Cloudflare hostname not yet published | 19 route/security tests | PARTIAL | tunnel cutover and remote E2E | publish existing tunnel route |
| R04 | Product diagnostics | §11 | iOS Settings | n/a | absent | absent | complete | n/a | iOS typecheck | PARTIAL | Web/macOS do not label route | add after native rollout if needed |
| R05 | Shared API/domain logic | §§3,7 | `packages/tessera-client`, Web `r2.ts`, iOS `api.ts` | contract | shared | shared renderer | shared | n/a | client 8/8; Web build | PASS | none | retain |
| R06 | Native iOS product | §7 | `ios/src/app` | canonical APIs | n/a | n/a | Chat/Jobs/Accounts/Memory/More | simulator-installed | Doctor 20/20; Debug+Release builds; render/restart | PARTIAL | authenticated E2E pending deployment/OIDC | deploy and sign in |
| R07 | Secure native auth | §7, security model | iOS `SessionProvider` | OIDC config/me | existing PKCE | existing PKCE | system AuthSession PKCE, Keychain refresh | OIDC redirect registration unverified | TS pass; generated plist inspected | BLOCKED_EXTERNAL | live consent/redirect | user sign-in |
| R08 | Native app lock | §7 | iOS `SessionProvider`, config plugins | n/a | n/a | n/a | cold/background lock | n/a | TS pass; Face ID plist present | PARTIAL | real-device biometric E2E | physical dogfood |
| R09 | Chat + streaming | §§5-7,14 | Web, Desktop renderer, iOS Chat/SSE | real coordinator | yes | yes | yes | real LiteLLM baseline | Web build; iOS TS | PARTIAL | authenticated iOS live stream | OIDC E2E |
| R10 | Server-side Jobs | §§7,17 | scheduler + iOS Jobs | yes | yes | yes | list/run/pause/resume | deployed baseline | backend baseline; iOS TS | PASS | create/edit remains Web/macOS | acceptable native workflow |
| R11 | Accounts/plugins | §§7,13,15,16 | plugin runtime + iOS Accounts/Plugins | yes | yes | yes | list/validate/disable/toggle | Gmail/RM auth incomplete | tests/baseline | PARTIAL | human provider authorization | consent checkpoints |
| R12 | Exact Actions | §§7,10,16 | coordinator + iOS review modal | yes | yes | yes | scope/payload/approve/cancel | deployed baseline | replay tests; client failover tests | PASS | live cross-client approval pending | authenticated E2E |
| R13 | Memory + Why | §§7,18 | server memory + iOS Memory | yes | yes | yes | evidence/stop-using | deployed baseline | persistence baseline; iOS TS | PASS | live provenance journey pending | authenticated E2E |
| R14 | Activity | §§5-7 | R2 API + iOS Activity | yes | yes | yes | yes | deployed baseline | Web build; iOS TS | PASS | none | retain |
| R15 | Notifications/deep links | §7, spec §37 | iOS Router/Notifications | external push not mandatory | in-app | native desktop | local permission/tap routes | no APNs service required | config/plugin + TS | PARTIAL | simulator click-through | run E2E |
| R16 | MCP/plugin boundary | §§13-16 | plugin abstractions, Gmail/RM modules | intact | consumes API | consumes API | consumes API | RM v0.5.38 | 769-test baseline | PASS | none | retain |
| R17 | Web/macOS regression-free | §§5-6 | shared Web `r2.ts` | unchanged | built | shared renderer | n/a | old package installed | R2 4/4; production build | PASS | repack after deploy | package at release |
| R18 | Native dependency security | §§31,35 | iOS package/README | n/a | n/a | n/a | minimal direct graph | n/a | Doctor; npm audit | PARTIAL | 22 SDK transitive advisories | upgrade when patched SDK exists |
| R19 | Deployed release convergence | §§22,26-29 | K8s config/image/GitOps | repo ready | old live | old installed | not installed | digest `58223131…` | last runtime baseline | MISSING | build/publish/reconcile | approval-gated release |
| R20 | Real cross-client E2E | §§18,27-30 | E2E reports | available | pending auth | pending auth | pending auth/build | old release | architecture only | BLOCKED_EXTERNAL | OIDC/provider consent plus deploy | execute after gates |
| R21 | Gmail real account | §15 | Gmail plugin/OAuth | implemented | workflow | workflow | shared Account | not authorized | plugin tests | BLOCKED_EXTERNAL | console/consent/safe target | human checkpoint |
| R22 | RM user + wife | §16 | RM MCP v0.5.38, account bindings | implemented | workflow | workflow | shared Accounts | connectors healthy | 159 RM tests baseline | BLOCKED_EXTERNAL | independent MFA/consent | account holders authorize |
| R23 | Diff closure artifacts | §§19-25,33-36 | this directory | n/a | n/a | n/a | n/a | n/a | artifact review | PASS | none | keep current |

Engineering-controlled open items are native build/simulator evidence and the approval-gated deployment/network change. No provider implementation was moved into Core/Broker.
