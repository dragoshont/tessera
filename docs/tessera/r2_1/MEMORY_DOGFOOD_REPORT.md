# Memory Dogfood Report

## Result

Durability/correction/Why: PASS
Influence through a real model: BLOCKED_EXTERNAL

## Verified Journey

1. Explicit Remember creates user-authored Evidence and current Assertion.
2. Data survives store/application reconstruction.
3. Correct creates a new current Assertion and preserves superseded history.
4. Why returns current/previous state, Evidence, and lineage.
5. Stop using removes the Assertion from current context without claiming backup erasure.
6. Normal model text cannot silently become `USER_ASSERTED`; model-requested remember/correct creates an exact Action requiring approval.

## Product UX

Memory exposes search, explicit Remember, status/timestamp, Why/Correct, history count, Evidence references, and Stop using. Copy distinguishes Memory from conversation history.

## Real Chat Checklist

After live model setup:

```text
Remember that I prefer concise technical summaries.
Restart Tessera.
How should you summarize technical reports for me?
```

Then correct the scoped preference and use Why. Until this live journey runs, no real-model compounding claim is made.