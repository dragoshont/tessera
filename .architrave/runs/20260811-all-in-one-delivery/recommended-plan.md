# Recommended Plan

## Summary

Promote the verified MCP-first artifact through the human-owned homelab process, then complete real account and recovery journeys without bypassing consent.

## Implementation Sequence

1. Obtain explicit approval for image publication and GitOps mutation.
2. Publish the immutable corrected image and update the private deployment with schema-v15 persistence and plugin modules.
3. Verify health/readiness, existing LiteLLM reachability and real model Chat.
4. Complete Gmail OAuth, RM user login/MFA and independent wife login/MFA.
5. Run real reads, Jobs, one approved safe Action per integration, provider re-read verification, restart, plugin disable and account disconnect dogfood.

## Test Strategy

Use `gates/deployed-alpha-checks.sh`, the mandate scorecard, read-only runtime evidence, and explicit safe-target variables. No mock can satisfy real E2E.

## Rollback / Recovery

Revert the private GitOps image/config diff; preserve the pre-migration database and use the verified isolated restore procedure. Never overwrite active SQLite in place.

## Human Approval Needed

Image publication, GitOps apply/reconcile/restart, secret access, Gmail OAuth, each RM login/MFA, and each real external side effect.
