# UI Control Audit

Audit combined route/component inspection, static searches, unit tests and a Playwright crawler at desktop and phone widths.

| Problem | Classification | Resolution | Evidence |
|---|---|---|---|
| `All connections` navigated to an undelivered disabled page | REMOVE | Route, nav item and page deleted | crawler traverses all primary routes |
| Account disable acted immediately | IMPLEMENT | Destructive confirmation added | product E2E |
| Job accepted a past schedule | IMPLEMENT | Client validation and disabled submit added | product E2E |
| Job cancel acted immediately | IMPLEMENT | Confirmation added | product E2E |
| Timezone accepted arbitrary text | IMPLEMENT | IANA validation and visible error added | unit/build checks |
| Plugin list lacked discovery | IMPLEMENT | Search, source status, trust/auth/sensitivity and Inspect added | product E2E |
| Chat exposed model setup before server discovery | IMPLEMENT | Automatic setup bootstrap and conditional setup center added | product E2E |
| Disabled controls during mutations | WORKING | Bounded busy states prevent duplicate writes | unit tests |

Crawler checks enforce primary navigation, accessible names, no `href="#"`, no production `coming soon`/`not implemented` copy and no console errors. The complete desktop/phone Playwright matrix passed 44/44; the focused current-product desktop slice passed 13/13.

External catalog results intentionally expose Inspect, not Install. This is a security decision, not a dead control: untrusted metadata cannot become executable code without a separately implemented server review/install workflow.