# Product Client Diff

| Feature | Web | macOS | iOS | Evidence / gap |
|---|---|---|---|---|
| Chat | PASS | PASS | PASS | iOS native screen/API; live auth pending |
| Streaming | PASS | PASS | PASS | iOS renders `text.delta`, resumes by sequence once, deduplicates IDs; live auth pending |
| Memory | PASS | PASS | PASS | same server objects |
| Why? | PASS | PASS | PASS | bounded evidence shown natively |
| Jobs | PASS | PASS | PASS | server-owned list/run |
| Job create | PASS | PASS | NOT_APPLICABLE | iOS intentionally focuses run/control; creation stays richer clients |
| Job pause/resume | PASS | PASS | PASS | optimistic version contract |
| Accounts | PASS | PASS | PASS | native Gmail OAuth + approved RM connectors + validate/disable |
| Gmail | BLOCKED_EXTERNAL | BLOCKED_EXTERNAL | BLOCKED_EXTERNAL | OAuth consent/safe target |
| Regina Maria | BLOCKED_EXTERNAL | BLOCKED_EXTERNAL | BLOCKED_EXTERNAL | user/wife independent authorization |
| Plugins | PASS | PASS | PASS | list and enable/disable native |
| Action approval | PASS | PASS | PASS | exact scope/payload modal, confirm, one-use server semantics |
| Activity | PASS | PASS | PASS | canonical feed |
| Settings | PASS | PASS | PASS | native security/connection settings |
| Connection diagnostics | PASS | PASS | PASS | Setup/server state in Web/macOS; detailed route/latency/version in iOS |
| Notifications | NOT_APPLICABLE | PASS | PASS | local permission/test/deep-link behavior delivered; external push not required by R2 spec |

`PASS` on iOS means implemented and statically/native-config validated. Authenticated live E2E remains separately tracked in `IOS_E2E.md` and is not implied by this matrix.
