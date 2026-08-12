# Requirements Diff

| Requirement | Actual state | Expected state | Gap | Owner | Fix | Test | Status |
|---|---|---|---|---|---|---|---|
| One canonical server | Server owns product state/scheduler | All clients share it | None in source | Engineering | Shared APIs only | architecture/full suite | PASS |
| Stable route identity | strict live descriptor | Verify before auth | None | Engineering | strict descriptor/shared client | live Web/iOS | PASS |
| Cloudflare remote path | proxied tunnel route live | Tunnel to Tessera service | None | Engineering | GitOps policy/helper | anycast TLS/descriptor | PASS |
| Easy first Chat | owner sign-in, auto-bootstrap and real Chat pass | auto-detect/bootstrap | None | Engineering | setup status/bootstrap | concurrency/live Chat | PASS |
| Existing integration truth | runtime/account states deployed separately | separate readiness/auth | User/provider auth | Shared | provider descriptors/status | tests/live readiness | AUTH CHECKPOINT |
| Real plugin search | static built-ins | local + public adapters | None in source | Engineering | server catalog/search UI | unit/E2E | PASS |
| Safe plugin supply chain | no external discovery | metadata only/review gate | None | Engineering | no arbitrary execution | architecture/tests | PASS |
| No dead controls | dead route/unsafe mutations | working/remove/confirm | None found | Engineering | route removal/confirm/validation | crawler/E2E | PASS |
| iOS loads | final Release renders verified server/sign-in | authenticated product | iOS user session | Shared | descriptor/release build | simulator/live | PASS/AUTH CHECKPOINT |
| Packaged macOS current | current Alpha packaged and installed | repack current Web | None before live server E2E | Engineering | Electron package | verify/package/install smoke | PASS |
| Real provider reads | runtime configured, accounts absent | safe reads after consent | User/provider auth | Shared | existing Connect flows | authenticated E2E | AUTH CHECKPOINT |
| Secondary account isolation | separate connectors | independent consent/state | None | Shared | no inferred Connected state | account E2E | PASS/UNAUTHORIZED |

Engineering-controlled `MISSING` = 0 and `PARTIAL` = 0. Remaining rows are explicit user/provider authorization checkpoints.