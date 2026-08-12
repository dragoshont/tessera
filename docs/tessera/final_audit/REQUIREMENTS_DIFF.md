# Requirements Diff

| Requirement | Actual state | Expected state | Gap | Owner | Fix | Test | Status |
|---|---|---|---|---|---|---|---|
| One canonical server | Server owns product state/scheduler | All clients share it | None in source | Engineering | Shared APIs only | architecture/full suite | PASS |
| Stable route identity | Old deployment lacks descriptor | Verify before auth | Deploy | Engineering | strict descriptor/shared client | focused tests | READY |
| Cloudflare remote path | Platform exists, hostname absent | Tunnel to Tessera service | Cutover | Engineering | GitOps policy/helper | live TLS/descriptor/SSE | READY |
| Easy first Chat | No canonical model profile | auto-detect/bootstrap | Deploy/authenticated call | Engineering | setup status/bootstrap | setup tests/Web E2E | READY |
| Existing integration truth | runtime and accounts conflated | separate readiness/auth | Deploy | Engineering | provider descriptors/status | tests/E2E | READY |
| Real plugin search | static built-ins | local + public adapters | None in source | Engineering | server catalog/search UI | unit/E2E | PASS |
| Safe plugin supply chain | no external discovery | metadata only/review gate | None | Engineering | no arbitrary execution | architecture/tests | PASS |
| No dead controls | dead route/unsafe mutations | working/remove/confirm | None found | Engineering | route removal/confirm/validation | crawler/E2E | PASS |
| iOS loads | bounded offline against stale server | authenticated product | Deploy and run | Engineering | descriptor/release build | simulator/live | READY |
| Packaged macOS current | current Alpha packaged and installed | repack current Web | None before live server E2E | Engineering | Electron package | verify/package/install smoke | PASS |
| Real provider reads | runtime configured, accounts absent | safe reads after consent | User/provider auth | Shared | existing Connect flows | authenticated E2E | AUTH CHECKPOINT |
| Secondary account isolation | separate connectors | independent consent/state | None | Shared | no inferred Connected state | account E2E | PASS/UNAUTHORIZED |

`READY` means implementation and deterministic checks pass but the authorized live cutover remains to be executed; it is not a final delivery claim.