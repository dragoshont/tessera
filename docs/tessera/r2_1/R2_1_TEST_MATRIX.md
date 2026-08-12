# R2.1 Test Matrix

## Final Deterministic Results

| Area | Result |
|---|---|
| Backend | 711/711 PASS |
| Web unit | 103/103 PASS |
| ESLint | PASS |
| Production build | PASS, bundle-size warning |
| Storybook static build | PASS, size warning |
| Playwright desktop/390px | 26/26 PASS |
| Compose config | PASS |
| Kubernetes render | PASS, 4 resources |
| kubeconform | PASS, 4 valid |
| NuGet vulnerable packages | 0 |
| npm production vulnerabilities | 0 |
| PII/secret scan | PASS |
| diff check | PASS |

## Reality-Shaped Coverage

- startup/readiness: active SQLite probe, scheduler heartbeat/failure/staleness, configuration states;
- SQLite: WAL/foreign keys/busy timeout, migrations v1-v12, online backup, integrity verification, isolated restore, overwrite refusal;
- streaming: arbitrary UTF-8 chunks, tools, continuation, missing DONE, cancellation, transient bounds/TTL, two readers, cross-owner denial, refresh reattach, durable final message;
- recovery: interrupted Chat and Job traces, completed read replay, pending Action, scheduler lease/fence;
- provider failures: missing/invalid auth, timeout, 429, malformed, oversized, unsafe credential-like output;
- GitHub: stable identity, classic scopes, absent fine-grained scopes, repository read proof, unverified write removal, allow-list and fixed origin;
- policy: plugin/account/conversation/Job grants, permission consistency, disable/revoke, exact approvals, replay/substitution denial;
- SSRF: public/loopback accepted for models; private, ULA, link-local, metadata blocked;
- UX: empty/setup/recovery states, approval, Jobs, Plugins, Accounts, 390px screenshots.

## External Matrix

| Check | Status |
|---|---|
| Real model chat/tool | BLOCKED_EXTERNAL |
| GitHub identity/read | BLOCKED_EXTERNAL |
| GitHub write | NOT_RUN_SAFE_MODE |

Contract tests do not promote these rows to PASS.