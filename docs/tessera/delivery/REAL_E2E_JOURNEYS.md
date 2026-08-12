# Real E2E Journeys

Current status before human GitOps/auth checkpoints:

| Journey | Status |
|---|---|
| Deploy new all-in-one image | BLOCKED: human GitOps gate |
| Stable LAN URL | PASS for old deployment |
| Real LiteLLM | Existing gateway PASS; new Tessera route not deployed |
| Gmail OAuth/read | AUTH_REQUIRED |
| Gmail safe approved send | NOT_RUN_SAFE_TARGET |
| RM user auth | Existing connector session available; new identity tool not deployed |
| RM user appointments/availability | BLOCKED until connector release + Tessera cutover |
| RM wife auth | AUTH_REQUIRED: account holder login/MFA |
| RM wife reads/availability | BLOCKED |
| RM booking/reschedule/cancel | NOT_RUN_SAFE_TARGET |
| Gmail/RM Jobs | Implemented and test-verified; not deployed |
| Restart persistence | Unit/integration verified; deployed restart not run |
| Host reboot | NOT_RUN: disruptive; stack restart preferred |

After deployment, run `gates/deployed-alpha-checks.sh`, then the mandate prompt set in deployed Chat. Do not report provider PASS from test doubles.