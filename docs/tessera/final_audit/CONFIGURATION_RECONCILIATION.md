# Configuration Reconciliation

Post-cutover state. `CONNECTED` is never inferred from configuration alone.

| Integration | Existing config discovered | Canonical product state at audit | Runtime/UI state | Fix |
|---|---|---|---|---|
| LiteLLM | Yes; real gateway and AKV-projected secret match | No model account/profile before owner auth | `READY_TO_CONNECT` after sign-in | Server validates and bootstraps account/profile/defaults idempotently |
| GitHub plugin | Yes; module installed | No ConnectedAccount | `AUTH_REQUIRED` | Provider setup descriptor and one Accounts connect path |
| Google OAuth | Yes; OAuth application configured | No Gmail ConnectedAccount | `READY_TO_CONNECT` | Distinguish runtime readiness from user consent |
| Gmail account | No persisted account at audit | None | Connect required | Existing OAuth flow remains canonical |
| Regina Maria MCP | Yes; two isolated connectors healthy | Plugin installed, no canonical accounts | Runtime Ready | Setup descriptor derives actual host configuration |
| My Regina Maria | Connector exists; account authorization not inferred | None | Connect/Reauthenticate | Per-account provider flow creates canonical state only after verification |
| Secondary Regina Maria account | Separate connector exists; no consent inferred | None | `NOT_CONNECTED`/auth checkpoint | Preserve isolated authorization and credential custody |
| Cloudflare Tunnel | Existing platform tunnel plus Tessera hostname | N/A | Live/healthy | Proxied DNS and namespace Service route verified through anycast |

The existing LiteLLM key is migrated to Azure Key Vault and projected through External Secrets without storing or printing it. Provider secrets never enter the product database or these audit artifacts.