# Runtime Observer

## Sources Used

Read-only Kubernetes/Flux/GitOps/tunnel/DNS/HTTP evidence and canonical SQLite metadata. Secret values were not printed or persisted.

## Observed State

- Running Tessera image is `04e1a046...297249` from reviewed custody-fix source `1eafb29`; private GitOps revision `4fd4dbf` is applied.
- Data/backup PVCs and one server scheduler replica are healthy.
- Five reviewed plugins are installed. After owner authentication, canonical state has one healthy model account, one enabled profile and both defaults.
- AKV-projected LiteLLM secret matches the existing live key without disclosure.
- Cloudflare Tunnel and proxied DNS route the canonical hostname to the Tessera namespace Service.
- Descriptor, backup, schema 15 and replacement-pod recovery pass.
- Authenticated Web setup and real persisted/streamed Chat pass (`TESSERA LIVE OK`).

## Mismatches

The reviewed-install source phase is not yet published/deployed. Provider accounts and authenticated macOS/iOS continuity remain checkpoints.

## Human Approval Items

Provider consent/MFA and physical-device signing only. Tessera-specific deployment/network mutation is authorized by the controlling mandate.
