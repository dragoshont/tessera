# Alpha Journeys

| Journey | Status | Evidence / completion step |
|---|---|---|
| 1 Fresh start | PASS internal | one-command startup; Chat setup CTA; configure through Settings |
| 2 Real Chat | BLOCKED_EXTERNAL | streaming/restart contract PASS; run live harness with model |
| 3 Durable Memory | PASS internal / live influence blocked | remember/restart/Why tests; repeat with live model |
| 4 Correction | PASS | current/superseded history and context selection |
| 5 GitHub connect | BLOCKED_EXTERNAL | identity/scope/health implementation PASS; connect real token |
| 6 GitHub read | BLOCKED_EXTERNAL | fixed-origin/read/evidence contract PASS; set repository and run harness |
| 7 Safe Action | NOT_RUN_SAFE_MODE | exact Action/approval/verification tests PASS; requires named sandbox opt-in |
| 8 Job from UI | PASS internal | creation/run/history and explicit Job Access |
| 9 Job from Chat | PASS contract | structured proposal requires product confirmation |
| 10 Scheduler restart | PASS | expired lease requeue, trace reset, unique occurrence |
| 11 Job + GitHub | BLOCKED_EXTERNAL | explicit grant and permission tests PASS |
| 12 Pending approval | PASS | refresh/restart waiting state and deterministic continuation |
| 13 Account revocation | PASS internal | capability denial and dependent Job BLOCKED; live revoke not performed |
| 14 Plugin disable | PASS | discovery/dispatch/Job health blocked immediately |
| 15 Backup/restore | PASS | representative isolated restore and source unchanged |

## Dogfood Sequence

1. `./scripts/devloop/up`
2. Sign in locally.
3. Configure a model in Settings.
4. Chat, Remember, restart, ask Why.
5. Create a run-now Job and inspect its run.
6. Connect GitHub and grant read to a conversation/Job.
7. Run `gates/live-alpha-checks.sh`.
8. Back up with `scripts/devloop/backup`.

No journey writes externally unless the explicit safe-write variables are supplied.