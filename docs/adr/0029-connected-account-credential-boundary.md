# ADR 0029 — ConnectedAccount metadata and credential custody boundary

- **Status:** Accepted (2026-08-10)

`ConnectedAccount` stores owner-scoped product metadata and opaque credential references only. Secret values remain behind existing `ICredentialStore` and `ICredentialWriter` implementations and never enter SQLite, logs, capability results, model context, or run artifacts. Multiple same-provider accounts are first-class; consequential ambiguity fails closed. Disable/revoke is checked immediately before dispatch.
