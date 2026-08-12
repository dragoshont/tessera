# Final Product Diff

Pre-cutover status: `PARTIAL`.

## Repository Complete

- Server-owned setup status and idempotent LiteLLM bootstrap.
- Provider-owned runtime readiness descriptors.
- Provider-neutral local/MCP Registry/provider-owned public catalog search.
- Shared strict route/auth client used by Web and native iOS.
- Web and iOS setup, Accounts readiness and Plugins search.
- Dead route removal, confirmations and input validation.
- Cloudflare-based private GitOps rollout prepared.

## Engineering-Controlled Open

1. Publish and deploy the current image digest.
2. Migrate the existing LiteLLM key to Key Vault without disclosure.
3. Add the Tessera hostname to the existing Cloudflare Tunnel.
4. Run authenticated live setup/Chat/canonical-state/restart checks.
5. Install and run the final iOS Release against the deployed descriptor.

The current macOS Electron product was repackaged, verified, installed in `/Applications`, and passed its installed renderer-ready smoke. The previous app remains available as a timestamped rollback copy.

## Human/Auth Checkpoints

- Gmail user consent if no valid account session exists.
- User Regina Maria authentication/reauthentication if required.
- Secondary Regina Maria account authentication/MFA, independently performed by its holder.
- Physical iPhone signing/device/cellular verification if unavailable to automation.

This file must be updated after cutover. `DELIVERED_ENGINEERING_AUTH_CHECKPOINTS_REMAIN` may be used only when every engineering-controlled item above is closed; `DELIVERED_E2E` additionally requires the real provider and cross-client scenarios.