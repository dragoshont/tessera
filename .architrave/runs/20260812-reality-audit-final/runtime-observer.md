# Runtime Observer

## Sources Used

Read-only Kubernetes/Flux/GitOps/tunnel/DNS/HTTP evidence and canonical SQLite metadata. Secret values were not printed or persisted.

## Observed State

- Running Tessera image is `835f28b2...44bc38` from final source `4e60505`; private GitOps revision `ca2b1e8` is applied.
- Data/backup PVCs and one server scheduler replica are healthy.
- Five reviewed plugins are installed. After owner authentication, canonical state has one healthy model account, one enabled profile and both defaults.
- AKV-projected LiteLLM secret matches the existing live key without disclosure.
- Cloudflare Tunnel and proxied DNS route the canonical hostname to the Tessera namespace Service.
- Descriptor, online backup integrity/schema 15, PVC continuity and replacement-pod recovery pass. A fresh encrypted off-node snapshot is observed; isolated restic restore is not claimed.
- Authenticated Web setup and real persisted/streamed Chat pass (`TESSERA LIVE OK`).

## Mismatches

No engineering-controlled runtime mismatch remains. Provider accounts and authenticated macOS/iOS continuity remain checkpoints.

## Human Approval Items

Provider consent/MFA and physical-device signing only. Tessera-specific deployment/network mutation is authorized by the controlling mandate.
