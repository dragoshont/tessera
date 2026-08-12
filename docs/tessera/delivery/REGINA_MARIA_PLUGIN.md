# Regina Maria Plugin

`regina-maria@1.0.0` is a curated Tessera plugin backed by fixed internal MCP connectors.

Capabilities include identity, appointment list/get, availability, proposal and execution for book/reschedule/cancel. Account connection selects an operator-configured profile label; no endpoint, password, cookie, or OTP is accepted from the browser. Session liveness plus main-profile identity are required before `CONNECTED`.

Account health is reconciled every 15 minutes. A parked/dead session becomes `AUTH_REQUIRED`, dependent Jobs block, and a legitimate re-seed can recover the same Account. A different provider identity moves the Account to `ERROR` rather than rebinding.

Default output retains scheduling logistics only. Medical history/results are not exposed by this plugin.