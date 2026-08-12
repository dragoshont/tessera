# R2 Account Model

`ConnectedAccount` is owner-scoped metadata: ID, provider/plugin ID, display name, external identity hint, lifecycle, server-generated opaque credential reference, permissions, capability bindings, health, last successful use, timestamps, and version. Lifecycle and legal transitions are defined in `R2_DATA_MODEL.md`.

Multiple accounts per provider are valid. The exact owner-bound custody write, compensation, empty-bundle revocation, and cleanup-retry contract is in `R2_API_CONTRACT.md`; clients never submit credential references. SQLite, logs, results, and model context contain only the opaque reference. Validation is adapter-specific (`GET /user` for GitHub). Disable/revoke is rechecked at dispatch and blocks future Chat/Job execution. Consequential ambiguity requires explicit account selection.
