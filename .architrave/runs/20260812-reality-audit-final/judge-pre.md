# Judge Gate 1

## Verdict

REVISE on commit `0d502a7`.

## Findings

- BLOCKER: concurrent model bootstrap could erase the winning deterministic credential through compensation.
- BLOCKER: Broker accepted plugin-supplied trust/install metadata and unsafe Inspect URLs.
- MAJOR: setup state trusted profile metadata without account/custody validation.
- MAJOR: bootstrap endpoint did not validate the client idempotency key.
- MAJOR: iOS notification UI implied Action/Job alert delivery while only sending a local test.
- MAJOR: setup/catalog routes were absent from the API contract and new UI states were absent from Storybook/design map.
- RELEASE: first CI run failed because the zero UUID example intentionally failed validation; a non-zero allowlisted placeholder is required for that smoke.

All findings above are addressed locally and await full post-fix gates/judge.
