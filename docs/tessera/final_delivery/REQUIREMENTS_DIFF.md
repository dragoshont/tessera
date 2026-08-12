# Requirements Diff

Baseline: `611af03`. Status reflects repository and last verified runtime evidence, not intent.

| ID | Requirement | Source mandate | Implementation location | Server | Web | macOS | iOS | Deployment | Test/E2E evidence | Status | Gap | Action |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| R01 | One canonical server/state | continuation §§3,18 | `src/`, SQLite schema v15 | yes | yes | yes | yes | deployed current release | persistence/restart live | PASS | none | retain |
| R02 | Stable server identity | §§8-10 | `BrokerHost`, `TesseraConfig`, ADR 0034 | implemented | canonical URL | canonical URL | strict UUID verify | live/no-store | backend 786; client 19; live descriptor | PASS | none | retain |
| R03 | Verified local/remote selection | §§8-12 | `packages/tessera-client` | descriptor | canonical URL | fixed canonical URL | canonical Cloudflare route with verified identity | proxied tunnel live | public-anycast descriptor | PASS | none | retain |
| R04 | Product diagnostics | §11 | Setup + iOS Settings | status/version | setup status | shared setup | detailed route/latency/version | live | Web/iOS evidence | PASS | none | retain |
| R05 | Shared API/domain logic | §§3,7 | `packages/tessera-client`, Web `r2.ts`, iOS `api.ts` | contract | shared | shared renderer | shared | n/a | client 19/19; Web build | PASS | none | retain |
| R06 | Native iOS product | §7 | `ios/src/app` | canonical APIs | n/a | n/a | Chat/Jobs/Accounts/Memory/More | final Release installed | verified-server render | PASS | owner auth | user sign-in |
| R07 | Secure native auth | §7, security model | iOS `SessionProvider` | OIDC config/me | PKCE | PKCE | system AuthSession PKCE, Keychain refresh | live provider reached | sign-in action/live Web redirect | AUTH CHECKPOINT | user session/MFA | user sign-in |
| R08 | Native app lock | §7 | iOS `SessionProvider`, config plugins | n/a | n/a | n/a | cold/background lock | n/a | source/build | AUTH CHECKPOINT | real-device biometric | physical dogfood |
| R09 | Chat + streaming | §§5-7,14 | Web, Desktop renderer, iOS Chat/SSE | real coordinator | yes | yes | yes | LiteLLM key projected/live | deterministic tests | AUTH CHECKPOINT | authenticated stream | user sign-in |
| R10 | Server-side Jobs | §§7,17 | scheduler + iOS Jobs | yes | yes | yes | list/run/pause/resume | deployed baseline | backend baseline; iOS TS | PASS | create/edit remains Web/macOS | acceptable native workflow |
| R11 | Accounts/plugins | §§7,13,15,16 | plugin runtime + iOS Accounts/Plugins | yes | yes | yes | list/validate/disable/toggle/search | runtime ready, accounts unauthenticated | tests/live registry | AUTH CHECKPOINT | provider authorization | consent checkpoints |
| R12 | Exact Actions | §§7,10,16 | coordinator + iOS review modal | yes | yes | yes | scope/payload/approve/cancel | deployed baseline | replay tests; client failover tests | PASS | live cross-client approval pending | authenticated E2E |
| R13 | Memory + Why | §§7,18 | server memory + iOS Memory | yes | yes | yes | evidence/stop-using | deployed baseline | persistence baseline; iOS TS | PASS | live provenance journey pending | authenticated E2E |
| R14 | Activity | §§5-7 | R2 API + iOS Activity | yes | yes | yes | yes | deployed baseline | Web build; iOS TS | PASS | none | retain |
| R15 | Notifications/deep links | §7, spec §37 | iOS Router/Notifications | external push not mandatory | in-app | native desktop | local permission/test routes | no APNs required | build/UI | PASS | signed-device click | physical dogfood |
| R16 | MCP/plugin boundary | §§13-16 | plugin abstractions, Gmail/RM modules | intact | consumes API | consumes API | consumes API | current | architecture/full 786 | PASS | none | retain |
| R17 | Web/macOS regression-free | §§5-6 | shared Web `r2.ts` | current | built | packaged/installed | n/a | current | 105/44/package smoke | PASS | none | retain |
| R18 | Native dependency security | §§31,35 | iOS package/README | n/a | n/a | n/a | minimal direct graph | n/a | Doctor/build | PASS | monitor SDK advisories | routine upgrades |
| R19 | Deployed release convergence | §§22,26-29 | K8s config/image/GitOps | custody fix | live | installed | installed | digest `04e1a046…` | CI/Flux/Cloudflare/restart | PASS | final reviewed-install image publication | publish after source gates |
| R20 | Real cross-client E2E | §§18,27-30 | E2E reports | available | owner Chat PASS | pending client session | pending client session | current | live Web model E2E | AUTH CHECKPOINT | provider and client sessions | continue after consent/sign-in |
| R21 | Gmail real account | §15 | Gmail plugin/OAuth | implemented | workflow | workflow | shared Account | not authorized | plugin tests | BLOCKED_EXTERNAL | console/consent/safe target | human checkpoint |
| R22 | RM user + wife | §16 | RM MCP v0.5.38, account bindings | implemented | workflow | workflow | shared Accounts | connectors healthy | 159 RM tests baseline | BLOCKED_EXTERNAL | independent MFA/consent | account holders authorize |
| R23 | Diff closure artifacts | §§19-25,33-36 | this directory | n/a | n/a | n/a | n/a | n/a | artifact review | PASS | none | keep current |

Engineering-controlled `MISSING` = 0 and `PARTIAL` = 0. Remaining checkpoints require user/provider authentication. No provider implementation moved into Core/Broker.
