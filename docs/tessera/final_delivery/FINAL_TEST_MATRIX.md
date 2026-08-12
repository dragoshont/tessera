# Final Test Matrix

| Lane | Current result |
|---|---|
| Backend | 786 PASS |
| Shared TypeScript client | 19 PASS |
| Web unit/build | 105 PASS; production build PASS |
| iOS typecheck / Expo Doctor | PASS; 20/20 |
| iOS native builds | Debug + standalone Release PASS |
| iOS simulator render/restart | PASS; verified live server/sign-in rendered |
| Web Playwright | 44 PASS across desktop and phone |
| Authenticated LiteLLM Chat | PASS (`TESSERA LIVE OK`) |
| Reviewed local install/public refusal | PASS |
| Desktop unit/security | 7 PASS |
| Electron dev launch | PASS |
| Packaged and installed app launch | PASS |
| Desktop npm audit | 0 vulnerabilities |
| Package secret scan | PASS |
| Public Kubernetes render/schema | 7/7 valid; full homelab baseline 521 unique, 0 invalid/errors |
| Live health/readiness/TLS/CORS | PASS |
| Live egress allow/deny | PASS |
| Live LiteLLM completion | PASS |
| Backup integrity/schema v15 | PASS |
| Full backend restart recovery | PASS |
| Hardened CI/image + Flux rollout | PASS |
| Cloudflare anycast descriptor/health | PASS |
| Real deployed authenticated/provider/cross-client E2E | AUTH CHECKPOINT |