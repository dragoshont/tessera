# Runtime Observer

## Sources Used

Read-only Kubernetes/Flux/GitOps/tunnel/DNS/HTTP evidence and canonical SQLite metadata. Secret values were not printed or persisted.

## Observed State

- Running Tessera image is the previous schema-v15 stateful release.
- Data/backup PVCs and one server scheduler replica are healthy.
- Five plugins are installed; canonical accounts/model profiles were empty at audit.
- Existing LiteLLM and two isolated Regina Maria runtimes are configured outside canonical account state.
- Existing two-replica Cloudflare Tunnel has no Tessera hostname at the pre-cutover checkpoint.

## Mismatches

- Descriptor route returns SPA HTML from the stale image.
- Public DNS/tunnel route does not yet provide the canonical remote path.
- Model/account bootstrap and current Web/native clients are not deployed.

## Human Approval Items

Provider consent/MFA and physical-device signing only. Tessera-specific deployment/network mutation is authorized by the controlling mandate.
