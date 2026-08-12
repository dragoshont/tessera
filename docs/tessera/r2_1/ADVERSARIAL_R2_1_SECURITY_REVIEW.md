# Adversarial R2.1 Security Review

## Verdict

PASS. No unresolved Critical/High security finding after remediation.

## Findings Closed

- transient SSE is bound to owner + conversation + execution and checked against durable execution control; cross-principal disclosed-ID test returns 404;
- provider output recursive DLP detects credential-like properties, generic token/secret/auth properties, plus GitHub/OpenAI/Slack/JWT token families before capability-result/Evidence persistence; unsafe results persist only `{}` failure data;
- fine-grained GitHub write is not inferred: allow-listed repository reads prove read only, and unverified write binding is removed;
- remote model egress uses connect-time public-or-loopback guard; loopback requires an explicit localhost/loopback-literal origin, while arbitrary DNS-to-loopback, private, ULA, metadata, link-local, multicast, and unspecified addresses are blocked;
- atomic read and approved-write reservations recheck exact Account binding and all canonical permissions, closing downgrade races after policy evaluation;
- restore cannot overwrite an existing or active destination;
- live writes require explicit boolean and exact matching repository target.

## Security Questions

1. Chat selects another owner’s Account: denied.
2. Job uses ungranted Account: denied.
3. Model requests ungranted capability: absent and dispatch-denied.
4. GitHub content injects tool authority: remains untrusted result data; repeated tool loop rejected.
5. Plugin result claims approval: cannot mint authorization.
6. Stale approval replay: denied/terminal receipt only for same authorization.
7. Payload changes after approval: hash/binding mismatch denied.
8. Disabled plugin direct execution: denied atomically.
9. Revoked Account queued Job: denied; Job health becomes BLOCKED.
10. Secrets in Evidence: recursive rejection before persistence; common token tests PASS.
11. Secrets in logs: structured identifiers/errors only; scans PASS.
12. Arbitrary URL SSRF: GitHub fixed origin; model public/loopback guarded.
13. Cross-user conversation/SSE: denied.
14. Cross-user Job read/mutation: owner derived server-side and denied.

## Residual Risk

Pattern DLP cannot mathematically identify every random secret. Defense also relies on bounded provider contracts, secret-property rejection, no prompt persistence, credential custody, Evidence provenance, and operator scans. Live provider behavior remains externally unverified.