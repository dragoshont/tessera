# Intake

## Understanding

Deliver the R2 Alpha as a continuously running homelab product with a real model, Gmail, two independently authorized RM accounts, Jobs, Actions, restart/recovery and MCP-first integrations. The provider-boundary correction became a prerequisite and is fully evidenced by run `20260811-plugin-boundary-correction`.

## Acceptance Criteria

1. MCP-first repository architecture, security, packaging, web, persistence and plan-only deployment gates pass.
2. Corrected image is published and applied only after explicit human approval.
3. Gmail OAuth and each RM account holder complete independent authorization.
4. Real Chat, reads, Jobs, approved safe writes, verification, restart and disable/disconnect dogfood pass.

## Grounding Sources

Canonical MCP-first mandate; `docs/tessera/delivery/**`; `docs/tessera/integrations/**`; run `20260811-plugin-boundary-correction`; current code/tests.

## Assumptions

Infrastructure apply, image publication, secret access, OAuth/MFA, account-holder consent, restart and real side effects require explicit human checkpoints.

## Blocking Questions

Approve image publication and the private homelab GitOps cutover; then complete Gmail OAuth and both RM authorization checkpoints when prompted.
