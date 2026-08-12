# Execution Model

## Principle

Models and workers may propose work. Deterministic policy and trusted human/broker paths authorize it. Capabilities perform it. Tessera durably records each materially different outcome.

## Action Lifecycle

```text
PROPOSED -> AUTHORIZED -> STARTED -> EXECUTION_SUCCEEDED
    |            |           |              |
    v            v           v              +-> PROVIDER_VERIFIED
 CANCELED      CANCELED    FAILED                  |
                            or                      +-> EXTERNALLY_CONFIRMED
                    RECONCILIATION_REQUIRED
```

Invalid transitions fail closed. `EXECUTION_SUCCEEDED` does not imply provider state or real-world outcome.

## Exact Authorization

Authorization binds owner, action ID, capability ID/version, payload hash, target scope, issue time, and expiry. It is one-time and exact; payload swap, target swap, expiry, replay, owner mismatch, or stale action version is denied.

SQLite atomically consumes approval and changes the exact action from `PROPOSED` to `AUTHORIZED` in one transaction. No half-consumed approval is committed.

`ActionAuthorizationService.IssueAsync` is an R0 mechanical contract, not a trusted issuance endpoint. Live execution requires broker authentication, deterministic policy, and independently obtained approval to compose this contract.

## Replay and Recovery

- Every action has a stable owner-scoped idempotency key.
- Mutable action/workflow updates use expected versions.
- Workflow checkpoints and action state survive restart.
- Unknown external outcomes require reconciliation, not blind retry.

R0 exposes reconciliation state but does not yet prove provider-specific timeout classification. The existing explicit failed-action retry test is infrastructure behavior, not permission to retry ambiguous live writes.

## No Live Writes

Kernel tests use deterministic/fake capabilities. This documentation does not activate provider integrations, cloud models, autonomous retries, browser automation, or live external mutations.